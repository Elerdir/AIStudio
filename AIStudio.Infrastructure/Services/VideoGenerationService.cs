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
/// <para>Umí i <b>dlouhé video</b> (<see cref="GenerateLongVideoAsync"/>) řetězením ~5s
/// segmentů (image→video z posledního snímku předchozího) a volitelný <b>2× ESRGAN upscale</b>
/// jako samostatný pass po uvolnění difuzního modelu z VRAM.</para>
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

    // ── Jedno video ────────────────────────────────────────────────────────────

    public async Task<VideoGenerationResult> GenerateAsync(
        VideoGenerationRequest request,
        IProgress<int>?        progress = null,
        CancellationToken      ct       = default)
    {
        if (request is null) return Fail("Chybí zadání.");
        if (!_comfy.IsRunning)
            return Fail("ComfyUI neběží — spusť ho v Nastavení a zkus znovu.");

        var modelsDir = ResolveModelsDirectory();

        var missing = _wanDeps.FindMissing(modelsDir, request.Model);
        if (missing.Count > 0)
            return new VideoGenerationResult(false, null,
                "Chybí Wan modely/závislosti — stáhni je a zkus znovu.", missing);

        var model    = request.Model;
        var negative = ResolveNegative(request.NegativePrompt);

        string? startImageName = null;
        if (model.Mode == WanVideoMode.ImageToVideo)
        {
            if (string.IsNullOrWhiteSpace(request.StartImagePath) || !File.Exists(request.StartImagePath))
                return Fail("Pro obrázek→video je potřeba vstupní obrázek.");
            try { startImageName = await _comfy.UploadImageAsync(request.StartImagePath!, ct); }
            catch (Exception ex)
            {
                Log.Warning(ex, "VideoGenerationService: upload vstupního obrázku selhal");
                return Fail("Nahrání vstupního obrázku do ComfyUI selhalo.");
            }
        }

        var outputDir = GetOutputDirectory();
        Directory.CreateDirectory(outputDir);

        string videoPath;
        try
        {
            var seg = await RunSegmentAsync(
                model, request.Prompt, negative, request.Width, request.Height, request.Length,
                request.Steps, request.Cfg, request.Seed, startImageName, request.Loras,
                saveLastFrame: false, outputDir,
                segPrefix: $"AIStudio_video_{DateTime.Now:yyyyMMdd_HHmmss}",
                progress, ct);
            if (seg.VideoPath is null) return Fail("ComfyUI nevrátil žádné video (zrušeno nebo timeout).");
            videoPath = seg.VideoPath;
        }
        catch (ComfyExecutionException ex) { return Fail("ComfyUI hlásí chybu při generování videa: " + ex.Message); }
        catch (OperationCanceledException) { return Fail("Generování videa zrušeno."); }
        catch (Exception ex)
        {
            Log.Error(ex, "VideoGenerationService: generování selhalo");
            return Fail("Generování videa selhalo: " + ex.Message);
        }

        // Volitelný post-proces (2× ESRGAN upscale → RIFE interpolace), samostatné passy.
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var finalPath = await PostProcessAsync(
            videoPath, request.Upscale, request.UpscaleModel,
            request.Interpolate, request.InterpolateMultiplier, request.Fps,
            outputDir, $"AIStudio_video_{stamp}", progress, ct);

        await SaveToGalleryAsync(finalPath, request.Prompt, model.Label, request.Seed,
            request.Width, request.Height, request.Steps, request.Cfg, ct);

        Log.Information("VideoGenerationService: hotovo → {Path}", finalPath);
        return new VideoGenerationResult(true, finalPath, null);
    }

    // ── Dlouhé video (řetězení segmentů) ───────────────────────────────────────

    public async Task<VideoGenerationResult> GenerateLongVideoAsync(
        LongVideoRequest              request,
        IProgress<LongVideoProgress>? progress = null,
        CancellationToken             ct       = default)
    {
        if (request is null) return Fail("Chybí zadání.");
        if (!_comfy.IsRunning)
            return Fail("ComfyUI neběží — spusť ho v Nastavení a zkus znovu.");

        var startFromImage = !string.IsNullOrWhiteSpace(request.StartImagePath) && File.Exists(request.StartImagePath);
        if (!startFromImage && request.T2VModel is null)
            return Fail("Pro dlouhé video z textu je potřeba text→video model (nebo začni vstupním obrázkem).");

        var modelsDir = ResolveModelsDirectory();

        // Závislosti: i2v model vždy; t2v model jen když 1. segment je z textu.
        var missing = new List<string>(_wanDeps.FindMissing(modelsDir, request.I2VModel));
        if (!startFromImage && request.T2VModel is not null)
            foreach (var m in _wanDeps.FindMissing(modelsDir, request.T2VModel))
                if (!missing.Contains(m)) missing.Add(m);
        if (missing.Count > 0)
            return new VideoGenerationResult(false, null,
                "Chybí Wan modely/závislosti — stáhni je a zkus znovu.", missing);

        var plan = VideoSegmentPlanner.Plan(request.TargetSeconds, request.Fps);
        var n    = plan.Count;
        var negative = ResolveNegative(request.NegativePrompt);

        var outputDir   = GetOutputDirectory();
        var segmentsDir = Path.Combine(outputDir, $"longvideo_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(segmentsDir);

        var segmentVideoPaths = new List<string>(n);
        string? carryFrameLocalPath = null;   // poslední snímek předchozího segmentu

        try
        {
            for (var k = 0; k < n; k++)
            {
                ct.ThrowIfCancellationRequested();
                var length   = plan[k];
                var isFirst  = k == 0;
                var useT2V   = isFirst && !startFromImage;
                var model    = useT2V ? request.T2VModel! : request.I2VModel;
                // seed: posuň po segmentech, ať není každý identický šum, ale zůstává deterministický
                var segSeed  = unchecked(request.Seed + k);

                Report(progress, k, n, $"Segment {k + 1}/{n} — generuji", basePercent(k, n, 0));

                // Vstupní obrázek segmentu: 1. segment = uživatelův obrázek; další = carry frame.
                string? startImageName = null;
                if (!useT2V)
                {
                    var localImg = isFirst ? request.StartImagePath! : carryFrameLocalPath!;
                    startImageName = await _comfy.UploadImageAsync(localImg, ct);
                }

                var inner = new Progress<int>(p =>
                    Report(progress, k, n, $"Segment {k + 1}/{n} — generuji", basePercent(k, n, p)));

                var seg = await RunSegmentAsync(
                    model, request.Prompt, negative, request.Width, request.Height, length,
                    request.Steps, request.Cfg, segSeed, startImageName, request.Loras,
                    saveLastFrame: k < n - 1, segmentsDir, $"segment_{k + 1:00}", inner, ct);

                if (seg.VideoPath is null)
                    return Fail($"Segment {k + 1}/{n} se nevygeneroval (zrušeno nebo timeout).");

                segmentVideoPaths.Add(seg.VideoPath);
                carryFrameLocalPath = seg.LastFramePath;

                if (k < n - 1 && carryFrameLocalPath is null)
                    return Fail($"Z segmentu {k + 1}/{n} se nepodařilo získat poslední snímek pro navázání.");
            }

            // Volitelný post-proces každého segmentu zvlášť (paměťově bezpečné — krátké klipy).
            if (request.Upscale || request.Interpolate)
            {
                var stage = request.Upscale && request.Interpolate ? "Vylepšuji segmenty (rozlišení + plynulost)…"
                          : request.Upscale ? "Zvyšuji rozlišení segmentů…"
                          : "Vyhlazuji pohyb segmentů…";
                Report(progress, n, n, stage, 90);
                for (var k = 0; k < segmentVideoPaths.Count; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    segmentVideoPaths[k] = await PostProcessAsync(
                        segmentVideoPaths[k], request.Upscale, request.UpscaleModel,
                        request.Interpolate, request.InterpolateMultiplier, request.Fps,
                        segmentsDir, $"segment_{k + 1:00}_pp", progress: null, ct);
                }
            }

            // Spojení do jednoho výsledného MP4 (ffmpeg concat, stream copy).
            Report(progress, n, n, "Spojuji segmenty…", 95);
            var finalPath = Path.Combine(outputDir, $"AIStudio_longvideo_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            var joined    = await FfmpegVideoJoiner.JoinAsync(segmentVideoPaths, finalPath, ResolvePythonPath(), ct);

            var galleryPath = joined && File.Exists(finalPath) ? finalPath : segmentVideoPaths[0];
            var effSeconds  = VideoSegmentPlanner.EffectiveSeconds(plan, request.Fps);
            var label       = $"{request.I2VModel.Label} · dlouhé {effSeconds:0.0}s ({n}×)";

            await SaveToGalleryAsync(galleryPath, request.Prompt, label, request.Seed,
                request.Width, request.Height, request.Steps, request.Cfg, ct);

            Report(progress, n, n, "Hotovo", 100);
            Log.Information("VideoGenerationService: dlouhé video hotovo → {Path} ({N} segmentů, spojeno={Joined})",
                galleryPath, n, joined);

            var note = joined
                ? null
                : "Spojení segmentů přes ffmpeg se nepovedlo — segmenty zůstaly samostatně ve složce vedle videa.";
            return new VideoGenerationResult(true, galleryPath, note);
        }
        catch (ComfyExecutionException ex) { return Fail("ComfyUI hlásí chybu při generování videa: " + ex.Message); }
        catch (OperationCanceledException) { return Fail("Generování dlouhého videa zrušeno."); }
        catch (Exception ex)
        {
            Log.Error(ex, "VideoGenerationService: dlouhé video selhalo");
            return Fail("Generování dlouhého videa selhalo: " + ex.Message);
        }
    }

    // ── Sdílené stavební bloky ─────────────────────────────────────────────────

    /// <summary>
    /// Vygeneruje jeden video klip (segment): sestaví t2v/i2v workflow, volitelně přidá uložení
    /// posledního snímku, zařadí do ComfyUI, počká, stáhne MP4 (a případně poslední snímek) a
    /// uloží je do <paramref name="outputDir"/>. Po dokončení uvolní VRAM. Vrací cesty na disku.
    /// </summary>
    private async Task<(string? VideoPath, string? LastFramePath)> RunSegmentAsync(
        WanVideoModel model, string prompt, string negative, int width, int height, int length,
        int steps, double cfg, long seed, string? startImageName, IReadOnlyList<LoraItem>? loras,
        bool saveLastFrame, string outputDir, string segPrefix,
        IProgress<int>? progress, CancellationToken ct)
    {
        Dictionary<string, object> workflow =
            model.Mode == WanVideoMode.ImageToVideo && startImageName is not null
                ? ComfyWorkflowBuilder.BuildWanImageToVideo(
                    model.DiffusionModel.FileName, startImageName, prompt, width, height, length,
                    steps, cfg, seed, negativePrompt: negative, filenamePrefix: segPrefix)
                : ComfyWorkflowBuilder.BuildWanTextToVideo(
                    model.DiffusionModel.FileName, prompt, width, height, length,
                    steps, cfg, seed, negativePrompt: negative, filenamePrefix: segPrefix);

        if (saveLastFrame)
            ComfyWorkflowBuilder.AppendWanLastFrameSave(workflow, length);

        if (loras is { Count: > 0 })
            ComfyWorkflowBuilder.InjectWanLoras(workflow, loras);

        ComfyGenerationResult? result;
        try
        {
            var promptId = await _comfy.QueuePromptAsync(workflow, ct);
            result       = await _comfy.WaitForResultAsync(promptId, progress, ct);
        }
        finally
        {
            await _comfy.FreeMemoryAsync(CancellationToken.None);
        }

        if (result is null || result.Images.Count == 0) return (null, null);

        var videoRef = result.Images.FirstOrDefault(IsVideoRef) ?? result.Images[0];
        var videoExt = Path.GetExtension(videoRef.Filename);
        if (string.IsNullOrEmpty(videoExt)) videoExt = ".mp4";
        var videoPath = Path.Combine(outputDir, segPrefix + videoExt);
        await DownloadToFileAsync(videoRef, videoPath, ct);

        string? lastFramePath = null;
        if (saveLastFrame)
        {
            var frameRef = result.Images.FirstOrDefault(r => !IsVideoRef(r));
            if (frameRef is not null)
            {
                lastFramePath = Path.Combine(outputDir, segPrefix + "_last.png");
                await DownloadToFileAsync(frameRef, lastFramePath, ct);
            }
        }

        return (videoPath, lastFramePath);
    }

    /// <summary>
    /// Aplikuje volitelné post-procesy na hotové video v pořadí <b>upscale → interpolace</b>
    /// (upscale dřív = běží na méně snímcích, je levnější). Každý je samostatný ComfyUI pass.
    /// Při selhání kteréhokoli kroku se vrací nejlepší dosažený výsledek (nikdy nevyhodí).
    /// </summary>
    private async Task<string> PostProcessAsync(
        string videoPath, bool upscale, string? upscaleModel,
        bool interpolate, int multiplier, int fps, string outputDir, string prefix,
        IProgress<int>? progress, CancellationToken ct)
    {
        var current = videoPath;

        if (upscale && !string.IsNullOrWhiteSpace(upscaleModel))
        {
            try
            {
                var hd = await RunUpscalePassAsync(current, upscaleModel!, fps, outputDir, prefix + "_hd", progress, ct);
                if (hd is not null) current = hd;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warning(ex, "VideoGenerationService: upscale pass selhal — ponechávám předchozí video"); }
        }

        if (interpolate)
        {
            try
            {
                var smooth = await RunInterpolatePassAsync(current, fps, multiplier, outputDir, prefix + "_smooth", progress, ct);
                if (smooth is not null) current = smooth;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warning(ex, "VideoGenerationService: interpolace selhala — ponechávám předchozí video"); }
        }

        return current;
    }

    /// <summary>
    /// Samostatný RIFE interpolační pass (dopočítá mezisnímky → plynulejší pohyb). Vrací cestu
    /// k plynulejšímu videu, nebo null při selhání.
    /// </summary>
    private async Task<string?> RunInterpolatePassAsync(
        string videoLocalPath, int fps, int multiplier, string outputDir, string prefix,
        IProgress<int>? progress, CancellationToken ct)
    {
        var workflow = ComfyWorkflowBuilder.BuildVideoInterpolatePass(videoLocalPath, fps, multiplier, filenamePrefix: prefix);

        ComfyGenerationResult? result;
        try
        {
            var promptId = await _comfy.QueuePromptAsync(workflow, ct);
            result       = await _comfy.WaitForResultAsync(promptId, progress, ct);
        }
        finally
        {
            await _comfy.FreeMemoryAsync(CancellationToken.None);
        }

        if (result is null || result.Images.Count == 0) return null;
        var videoRef = result.Images.FirstOrDefault(IsVideoRef) ?? result.Images[0];
        var ext      = Path.GetExtension(videoRef.Filename);
        if (string.IsNullOrEmpty(ext)) ext = ".mp4";
        var path = Path.Combine(outputDir, prefix + ext);
        await DownloadToFileAsync(videoRef, path, ct);
        return path;
    }

    /// <summary>
    /// Samostatný ESRGAN upscale pass nad hotovým MP4 (Wan model je už uvolněn z VRAM).
    /// Vrací cestu k HD videu, nebo null při selhání.
    /// </summary>
    private async Task<string?> RunUpscalePassAsync(
        string videoLocalPath, string upscaleModel, int fps, string outputDir, string prefix,
        IProgress<int>? upscaleProgress, CancellationToken ct)
    {
        var workflow = ComfyWorkflowBuilder.BuildVideoUpscalePass(videoLocalPath, upscaleModel, fps, filenamePrefix: prefix);

        ComfyGenerationResult? result;
        try
        {
            var promptId = await _comfy.QueuePromptAsync(workflow, ct);
            result       = await _comfy.WaitForResultAsync(promptId, upscaleProgress, ct);
        }
        finally
        {
            await _comfy.FreeMemoryAsync(CancellationToken.None);
        }

        if (result is null || result.Images.Count == 0) return null;
        var videoRef = result.Images.FirstOrDefault(IsVideoRef) ?? result.Images[0];
        var ext      = Path.GetExtension(videoRef.Filename);
        if (string.IsNullOrEmpty(ext)) ext = ".mp4";
        var path = Path.Combine(outputDir, prefix + ext);
        await DownloadToFileAsync(videoRef, path, ct);
        return path;
    }

    private async Task DownloadToFileAsync(ComfyImageRef r, string targetPath, CancellationToken ct)
    {
        var bytes = await _comfy.DownloadImageAsync(r.Filename, r.Subfolder, r.Type, ct);
        await File.WriteAllBytesAsync(targetPath, bytes, ct);
    }

    private async Task SaveToGalleryAsync(
        string filePath, string prompt, string modelLabel, long seed,
        int width, int height, int steps, double cfg, CancellationToken ct)
    {
        var record = new ImageRecord(
            Id:          Guid.NewGuid().ToString(),
            FilePath:    filePath,
            Prompt:      prompt,
            ModelName:   modelLabel,
            Seed:        seed,
            Width:       width,
            Height:      height,
            Steps:       steps,
            Cfg:         cfg,
            Sampler:     ComfyWorkflowBuilder.DefaultWanSampler,
            Scheduler:   ComfyWorkflowBuilder.DefaultWanScheduler,
            GeneratedAt: DateTime.Now,
            MediaType:   MediaTypes.Video);

        try { await _repo.SaveImageAsync(record); }
        catch (Exception ex) { Log.Warning(ex, "VideoGenerationService: SaveImageAsync selhalo — soubor je na disku"); }
    }

    // ── Drobné ─────────────────────────────────────────────────────────────────

    private static bool IsVideoRef(ComfyImageRef r)
    {
        var ext = Path.GetExtension(r.Filename);
        return ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveNegative(string? negative) =>
        string.IsNullOrWhiteSpace(negative) ? ComfyWorkflowBuilder.DefaultWanNegative : negative!;

    /// <summary>Overall procenta: segment k (0-based) s vnitřními p %, rezerva 0–90 % pro generování.</summary>
    private static int basePercent(int k, int n, int innerP) =>
        n <= 0 ? 0 : (int)Math.Clamp(((k + innerP / 100.0) / n) * 90.0, 0, 90);

    private static void Report(IProgress<LongVideoProgress>? progress, int k, int n, string stage, int overall) =>
        progress?.Report(new LongVideoProgress(overall, Math.Min(k + 1, n), n, stage));

    private static VideoGenerationResult Fail(string msg) => new(false, null, msg);

    private string GetOutputDirectory() =>
        !string.IsNullOrEmpty(_outputDirOverride) ? _outputDirOverride : AppPaths.DefaultImagesDirectory;

    private string ResolveModelsDirectory() =>
        !string.IsNullOrEmpty(_modelsDirOverride)
            ? _modelsDirOverride
            : AppPaths.ResolveModelsDirectory(_settings.Settings.ModelsDirectory);

    private string? ResolvePythonPath() =>
        string.IsNullOrWhiteSpace(_settings.Settings.PythonPath) ? null : _settings.Settings.PythonPath;
}
