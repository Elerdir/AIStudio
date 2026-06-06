using System.Runtime.InteropServices;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Vestavěný generátor obrázků nad <c>stable-diffusion.cpp</c> (viz <c>docs/native-generator-design.md</c>).
/// Lazy probe nativní knihovny; když chybí (dnes všude — přibalí se ve Fázi 2), hlásí
/// <c>Status.IsAvailable = false</c> a generování graceful selže (UI fallbackne na ComfyUI).
/// Všechna nativní volání jsou obalená try/catch, takže chybějící/nesedící lib nezpůsobí pád.
///
/// <para><b>Fáze 1:</b> kompletní managed pipeline (load → txt2img → PNG → uložit) je zapojená;
/// reálná nativní inference + GPU backendy + ověření P/Invoke signatur = Fáze 2 (vyžaduje
/// runtime na stroji s přibalenou nativní libou).</para>
/// </summary>
public sealed class NativeImageGenerator : INativeImageGenerator
{
    private readonly string?       _outputDirOverride;
    private readonly bool          _libPresent;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IntPtr           _ctx = IntPtr.Zero;
    private string?          _loadedModelPath;
    private NativeGenBackend _backend = NativeGenBackend.Cpu;

    public NativeImageGenerator(string? outputDirOverride = null)
    {
        _outputDirOverride = outputDirOverride;
        _libPresent        = SafeIsLibraryPresent();
    }

    private static bool SafeIsLibraryPresent()
    {
        try { return StableDiffusionInterop.IsLibraryPresent(); }
        catch (Exception ex) { Log.Debug(ex, "NativeImageGenerator: probe nativní knihovny selhal"); return false; }
    }

    public NativeGeneratorStatus Status => _libPresent
        ? new(true, _backend, $"stable-diffusion.cpp ({_backend})")
        : new(false, NativeGenBackend.Cpu, "nedostupné",
              "Vestavěný generátor zatím není přibalený (nativní knihovna stable-diffusion chybí). " +
              "Použij ComfyUI — vestavěná inference přijde v další fázi.");

    public bool IsModelLoaded => _ctx != IntPtr.Zero;

    public async Task LoadModelAsync(string modelPath, NativeGenBackend backend, CancellationToken ct = default)
    {
        if (!_libPresent)
            throw new InvalidOperationException("Vestavěný generátor není dostupný (chybí nativní knihovna stable-diffusion).");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            throw new FileNotFoundException("Model nenalezen.", modelPath);

        await _gate.WaitAsync(ct);
        try
        {
            if (_ctx != IntPtr.Zero && _loadedModelPath == modelPath) return;  // už načtený
            UnloadInternal();
            _backend = backend;

            await Task.Run(() =>
            {
                _ctx = StableDiffusionInterop.NewSdCtx(
                    modelPath, "", "", "", "", "", "", "", "", "", "",
                    vaeDecodeOnly: false, vaeTiling: false, freeParamsImmediately: false,
                    nThreads: Math.Max(1, Environment.ProcessorCount),
                    wtype: -1, rngType: 0, schedule: 0,
                    keepClipOnCpu: false, keepControlNetCpu: false, keepVaeOnCpu: false,
                    diffusionFlashAttn: false);
            }, ct);

            if (_ctx == IntPtr.Zero)
                throw new InvalidOperationException("Načtení modelu do stable-diffusion.cpp selhalo (null kontext).");
            _loadedModelPath = modelPath;
            Log.Information("NativeImageGenerator: model načten {Path} ({Backend})", modelPath, _backend);
        }
        finally { _gate.Release(); }
    }

    public async Task<NativeImageResult> GenerateAsync(
        NativeImageRequest request, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (request is null) return new(false, Array.Empty<string>(), "Chybí zadání.");
        if (!_libPresent)   return new(false, Array.Empty<string>(), Status.UnavailableReason);
        if (_ctx == IntPtr.Zero) return new(false, Array.Empty<string>(), "Model není načtený — zavolej LoadModelAsync.");

        await _gate.WaitAsync(ct);
        try
        {
            progress?.Report(0);
            var sdSampler = NativeSamplerMap.ToSdCpp(request.SamplerName);
            var method    = (int)StableDiffusionInterop.SampleMethodFromName(sdSampler);
            var batch     = Math.Max(1, request.BatchCount);

            var resultPtr = IntPtr.Zero;
            await Task.Run(() =>
            {
                resultPtr = StableDiffusionInterop.Txt2Img(
                    _ctx, request.Prompt ?? "", request.NegativePrompt ?? "",
                    clipSkip: -1, cfgScale: (float)request.CfgScale, guidance: 3.5f, eta: 0f,
                    width: request.Width, height: request.Height, sampleMethod: method, sampleSteps: request.Steps,
                    seed: request.Seed, batchCount: batch,
                    controlCond: IntPtr.Zero, controlStrength: 0.9f, styleStrength: 0f,
                    normalizeInput: false, inputIdImagesPath: "",
                    skipLayers: IntPtr.Zero, skipLayersCount: (nuint)0, slgScale: 0f, skipLayerStart: 0.01f, skipLayerEnd: 0.2f);
            }, ct);
            progress?.Report(95);

            if (resultPtr == IntPtr.Zero)
                return new(false, Array.Empty<string>(), "Generování nevrátilo žádný obrázek.");

            var outDir = GetOutputDirectory();
            Directory.CreateDirectory(outDir);
            var stamp   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var imgSize = Marshal.SizeOf<StableDiffusionInterop.SdImage>();
            var paths   = new List<string>(batch);

            for (var i = 0; i < batch; i++)
            {
                var img = Marshal.PtrToStructure<StableDiffusionInterop.SdImage>(resultPtr + i * imgSize);
                if (img.Data == IntPtr.Zero || img.Width == 0 || img.Height == 0) continue;

                var len    = (int)(img.Width * img.Height * img.Channel);
                var pixels = new byte[len];
                Marshal.Copy(img.Data, pixels, 0, len);

                var png  = PngEncoder.Encode(pixels, (int)img.Width, (int)img.Height, (int)img.Channel);
                var file = Path.Combine(outDir, $"AIStudio_native_{stamp}_{i}.png");
                await File.WriteAllBytesAsync(file, png, ct);
                paths.Add(file);
            }

            // POZN Fáze 2: uvolnit sd_image_t buffery (sd.cpp je alokuje malloc-em) — vyžaduje
            // přibalenou libu pro ověření správného free; teď se sem stejně nedostaneme.

            progress?.Report(100);
            return paths.Count > 0
                ? new(true, paths)
                : new(false, Array.Empty<string>(), "Žádný obrázek se neuložil.");
        }
        catch (DllNotFoundException ex)        { return new(false, Array.Empty<string>(), "Nativní knihovna nenalezena: " + ex.Message); }
        catch (EntryPointNotFoundException ex) { return new(false, Array.Empty<string>(), "Nativní funkce nenalezena (verze knihovny nesedí): " + ex.Message); }
        catch (OperationCanceledException)     { return new(false, Array.Empty<string>(), "Generování zrušeno."); }
        catch (Exception ex)
        {
            Log.Error(ex, "NativeImageGenerator: generování selhalo");
            return new(false, Array.Empty<string>(), "Generování selhalo: " + ex.Message);
        }
        finally { _gate.Release(); }
    }

    public Task UnloadAsync()
    {
        UnloadInternal();
        return Task.CompletedTask;
    }

    private void UnloadInternal()
    {
        if (_ctx != IntPtr.Zero)
        {
            try { StableDiffusionInterop.FreeSdCtx(_ctx); }
            catch (Exception ex) { Log.Warning(ex, "NativeImageGenerator: free_sd_ctx selhal"); }
            _ctx = IntPtr.Zero;
        }
        _loadedModelPath = null;
    }

    private string GetOutputDirectory() =>
        !string.IsNullOrEmpty(_outputDirOverride) ? _outputDirOverride : AppPaths.DefaultImagesDirectory;
}
