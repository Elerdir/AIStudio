using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Generuje video přes Wan 2.1: ověří běžící ComfyUI + přítomnost závislostí, sestaví
/// Wan workflow (<see cref="ComfyWorkflowBuilder"/>), zařadí do ComfyUI, počká na MP4,
/// uloží ho do výstupní složky a zapíše do galerie jako <c>MediaType=video</c>.
///
/// <para>Velké modely (jednotky až desítky GB) <b>nestahuje sám</b> — když chybí, vrátí
/// <see cref="VideoGenerationResult.MissingDependencies"/> a UI nabídne explicitní stažení
/// přes <see cref="IWanDependencyService"/> (s vlastním progress UI).</para>
/// </summary>
public sealed class VideoGenerationService : IVideoGenerationService
{
    private readonly IComfyService          _comfy;
    private readonly IWanDependencyService  _wanDeps;
    private readonly IImageRepository       _repo;
    private readonly ISettingsService       _settings;
    private readonly string?                _outputDirOverride;
    private readonly string?                _modelsDirOverride;

    public VideoGenerationService(
        IComfyService         comfy,
        IWanDependencyService wanDeps,
        IImageRepository      repo,
        ISettingsService      settings,
        string?               outputDirOverride = null,
        string?               modelsDirOverride = null)
    {
        _comfy             = comfy;
        _wanDeps           = wanDeps;
        _repo              = repo;
        _settings          = settings;
        _outputDirOverride = outputDirOverride;
        _modelsDirOverride = modelsDirOverride;
    }

    public async Task<VideoGenerationResult> GenerateAsync(
        VideoGenerationRequest request,
        IProgress<int>?        progress = null,
        CancellationToken      ct       = default)
    {
        if (request is null) return Fail("Chybí zadání.");
        if (!_comfy.IsRunning)
            return Fail("ComfyUI neběží — spusť ho v Nastavení a zkus znovu.");

        var modelsDir = ResolveModelsDirectory();

        // Velké modely nestahujeme implicitně — když chybí, ať to vyřeší UI.
        var missing = _wanDeps.FindMissing(modelsDir, request.Model);
        if (missing.Count > 0)
            return new VideoGenerationResult(false, null,
                "Chybí Wan modely/závislosti — stáhni je a zkus znovu.", missing);

        var model    = request.Model;
        var negative = string.IsNullOrWhiteSpace(request.NegativePrompt)
            ? ComfyWorkflowBuilder.DefaultWanNegative
            : request.NegativePrompt!;

        Dictionary<string, object> workflow;
        try
        {
            if (model.Mode == WanVideoMode.ImageToVideo)
            {
                if (string.IsNullOrWhiteSpace(request.StartImagePath) || !File.Exists(request.StartImagePath))
                    return Fail("Pro obrázek→video je potřeba vstupní obrázek.");

                string uploaded;
                try { uploaded = await _comfy.UploadImageAsync(request.StartImagePath!, ct); }
                catch (Exception ex)
                {
                    Log.Warning(ex, "VideoGenerationService: upload vstupního obrázku selhal");
                    return Fail("Nahrání vstupního obrázku do ComfyUI selhalo.");
                }

                workflow = ComfyWorkflowBuilder.BuildWanImageToVideo(
                    model.DiffusionModel.FileName, uploaded, request.Prompt,
                    request.Width, request.Height, request.Length, request.Steps, request.Cfg, request.Seed,
                    negativePrompt: negative, fps: request.Fps, filenamePrefix: "AIStudio_video");
            }
            else
            {
                workflow = ComfyWorkflowBuilder.BuildWanTextToVideo(
                    model.DiffusionModel.FileName, request.Prompt,
                    request.Width, request.Height, request.Length, request.Steps, request.Cfg, request.Seed,
                    negativePrompt: negative, fps: request.Fps, filenamePrefix: "AIStudio_video");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VideoGenerationService: sestavení workflow selhalo");
            return Fail("Sestavení video workflow selhalo: " + ex.Message);
        }

        // Zařaď + počkej na výsledek
        string promptId;
        ComfyGenerationResult? result;
        try
        {
            promptId = await _comfy.QueuePromptAsync(workflow, ct);
            result   = await _comfy.WaitForResultAsync(promptId, progress, ct);
        }
        catch (ComfyExecutionException ex)
        {
            Log.Warning(ex, "VideoGenerationService: ComfyUI execution error");
            return Fail("ComfyUI hlásí chybu při generování videa: " + ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail("Generování videa zrušeno.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VideoGenerationService: generování selhalo");
            return Fail("Generování videa selhalo: " + ex.Message);
        }
        finally
        {
            // Uvolni VRAM (Wan 14B je velký) — soubor je už v ComfyUI output složce.
            await _comfy.FreeMemoryAsync(CancellationToken.None);
        }

        if (result is null || result.Images.Count == 0)
            return Fail("ComfyUI nevrátil žádné video (zrušeno nebo timeout).");

        try
        {
            var outRef = result.Images[0];   // VHS_VideoCombine vrací mp4 pod "gifs" → ComfyImageRef
            var bytes  = await _comfy.DownloadImageAsync(outRef.Filename, outRef.Subfolder, outRef.Type, ct);

            var outputDir = GetOutputDirectory();
            Directory.CreateDirectory(outputDir);
            var ext      = Path.GetExtension(outRef.Filename);
            if (string.IsNullOrEmpty(ext)) ext = ".mp4";
            var fileName = $"AIStudio_video_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
            var filePath = Path.Combine(outputDir, fileName);
            await File.WriteAllBytesAsync(filePath, bytes, ct);

            var record = new ImageRecord(
                Id:          Guid.NewGuid().ToString(),
                FilePath:    filePath,
                Prompt:      request.Prompt,
                ModelName:   model.Label,
                Seed:        request.Seed,
                Width:       request.Width,
                Height:      request.Height,
                Steps:       request.Steps,
                Cfg:         request.Cfg,
                Sampler:     ComfyWorkflowBuilder.DefaultWanSampler,
                Scheduler:   ComfyWorkflowBuilder.DefaultWanScheduler,
                GeneratedAt: DateTime.Now,
                MediaType:   MediaTypes.Video);

            try { await _repo.SaveImageAsync(record); }
            catch (Exception ex) { Log.Warning(ex, "VideoGenerationService: SaveImageAsync selhalo — soubor je na disku"); }

            Log.Information("VideoGenerationService: hotovo → {Path}", filePath);
            return new VideoGenerationResult(true, filePath, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VideoGenerationService: uložení videa selhalo");
            return Fail("Uložení videa selhalo: " + ex.Message);
        }
    }

    private static VideoGenerationResult Fail(string msg) => new(false, null, msg);

    private string GetOutputDirectory() =>
        !string.IsNullOrEmpty(_outputDirOverride) ? _outputDirOverride : AppPaths.DefaultImagesDirectory;

    private string ResolveModelsDirectory() =>
        !string.IsNullOrEmpty(_modelsDirOverride)
            ? _modelsDirOverride
            : AppPaths.ResolveModelsDirectory(_settings.Settings.ModelsDirectory);
}
