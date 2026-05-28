using System.Diagnostics;
using Serilog;
using AIStudio.Core.Interfaces;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Default implementace <see cref="ILoraCaptionService"/> — volá sd-scripts
/// <c>finetune/make_captions.py</c> (BLIP) nebo <c>finetune/tag_images_by_wd14_tagger.py</c>
/// (WD14) přes ComfyUI Python venv.
///
/// <para>Strategie:</para>
/// <list type="number">
/// <item>Zkopírujeme obrázky do dočasné složky (sd-scripts script jede přes adresář).</item>
/// <item>Spustíme Python skript s <c>--caption_extension .txt</c> — výstupní popisky
/// vyleze jako sidecary <c>image.txt</c>.</item>
/// <item>Načteme všechny <c>.txt</c> a vrátíme jako dictionary.</item>
/// <item>Uklidíme dočasné soubory.</item>
/// </list>
///
/// <para>BLIP-large váží ~990 MB. Při prvním spuštění HuggingFace transformers
/// auto-stáhne do <c>%USERPROFILE%\.cache\huggingface\</c>. Další spuštění je rychlé.</para>
/// </summary>
public sealed class BlipCaptionService : ILoraCaptionService
{
    private readonly ILoraTrainerDependencyService _deps;
    private readonly Func<string?> _resolvePythonExe;

    /// <summary>Dočasná pracovní složka pro batch captioning.</summary>
    private static readonly string WorkingDirRoot =
        Path.Combine(AIStudio.Core.Services.AppPaths.AppDataRoot, "captioning");

    public bool IsCaptioning { get; private set; }

    public BlipCaptionService(
        ILoraTrainerDependencyService deps,
        Func<string?>                 resolvePythonExe)
    {
        _deps             = deps;
        _resolvePythonExe = resolvePythonExe;
    }

    public async Task<IReadOnlyDictionary<string, string>> CaptionAsync(
        IReadOnlyList<string>       imagePaths,
        string                      style    = "blip",
        IProgress<CaptionProgress>? progress = null,
        CancellationToken           ct       = default)
    {
        if (IsCaptioning)
            throw new InvalidOperationException("Auto-captioning už běží.");

        if (imagePaths.Count == 0) return new Dictionary<string, string>();

        var pythonExe = _resolvePythonExe()
            ?? throw new InvalidOperationException(
                "Python interpreter z ComfyUI nebyl nalezen — dokonči ComfyUI instalaci.");

        // Zajistíme závislosti — pokud sd-scripts ještě nejsou, doinstalují se teď
        await _deps.EnsureAllAsync(pythonExe, null, ct);
        var sdScriptsDir = _deps.SdScriptsDirectory
            ?? throw new InvalidOperationException("sd-scripts dir nedostupný");

        IsCaptioning = true;
        string? workDir = null;

        try
        {
            // 1) Nakopírovat obrázky do work dir (sd-scripts script projde celou složku)
            Directory.CreateDirectory(WorkingDirRoot);
            workDir = Path.Combine(WorkingDirRoot, $"batch_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(workDir);

            var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < imagePaths.Count; i++)
            {
                var src = imagePaths[i];
                if (!File.Exists(src)) continue;
                var ext  = Path.GetExtension(src);
                var dest = Path.Combine(workDir, $"{i:D4}{ext}");
                File.Copy(src, dest);
                nameMap[$"{i:D4}"] = src;   // mapa work-stub → originál path
            }

            progress?.Report(new CaptionProgress(0, imagePaths.Count, string.Empty, "Načítám BLIP model…"));

            // 2) Spustit Python script (BLIP nebo WD14)
            var (script, args) = BuildCommand(workDir, sdScriptsDir, style);
            await RunPythonAsync(pythonExe, sdScriptsDir, args, ct);

            // 3) Načíst captions z .txt sidecarů
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var done   = 0;
            foreach (var txtFile in Directory.EnumerateFiles(workDir, "*.txt"))
            {
                ct.ThrowIfCancellationRequested();

                var stub = Path.GetFileNameWithoutExtension(txtFile);
                if (!nameMap.TryGetValue(stub, out var originalPath)) continue;

                var caption = (await File.ReadAllTextAsync(txtFile, ct)).Trim();
                if (!string.IsNullOrWhiteSpace(caption))
                    result[originalPath] = caption;

                done++;
                progress?.Report(new CaptionProgress(
                    done, imagePaths.Count,
                    Path.GetFileName(originalPath),
                    Truncate(caption, 80)));
            }

            Log.Information("BlipCaptionService: vygenerováno {Done}/{Total} popisků (style={Style})",
                result.Count, imagePaths.Count, style);

            return result;
        }
        finally
        {
            IsCaptioning = false;
            // Uklid working dir
            if (workDir is not null && Directory.Exists(workDir))
            {
                try { Directory.Delete(workDir, recursive: true); }
                catch (Exception ex) { Log.Warning(ex, "BlipCaptionService: úklid {Dir} selhal", workDir); }
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string Script, string Args) BuildCommand(
        string workDir, string sdScriptsDir, string style)
    {
        if (string.Equals(style, "wd14", StringComparison.OrdinalIgnoreCase))
        {
            // WD14 tagger pro anime — vrací tagy oddělené čárkami, hodí se pro anime
            // LoRA. Model ~300 MB, auto-stažený při prvním běhu.
            var script = Path.Combine(sdScriptsDir, "finetune", "tag_images_by_wd14_tagger.py");
            var args   = $"\"{script}\" \"{workDir}\" --caption_extension .txt --batch_size 4 " +
                         $"--general_threshold 0.35 --character_threshold 0.35 --max_data_loader_n_workers 2";
            return (script, args);
        }

        // BLIP large captioning — vrací 1-2 věty popisu, default pro fotorealistic
        var blipScript = Path.Combine(sdScriptsDir, "finetune", "make_captions.py");
        var blipArgs   = $"\"{blipScript}\" \"{workDir}\" --caption_extension .txt --batch_size 4 " +
                         $"--max_data_loader_n_workers 2 --beam_search --max_length 75";
        return (blipScript, blipArgs);
    }

    private static async Task RunPythonAsync(
        string pythonExe, string workingDir, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = pythonExe,
            Arguments              = args,
            WorkingDirectory       = workingDir,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) Log.Debug("[caption] {Line}", e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Log.Debug("[caption-err] {Line}", e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"Captioning skript selhal (exit code {p.ExitCode}). Detail v debug logu.");
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" :
        s.Length <= max ? s : s[..max] + "…";
}
