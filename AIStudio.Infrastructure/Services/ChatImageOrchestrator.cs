using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Provádí čtení uživatelské zprávy → vygenerovaný obrázek pro chat use-case.
/// MVP scope:
/// <list type="bullet">
/// <item>SD/SDXL safetensors txt2img + img2img</item>
/// <item>FLUX safetensors txt2img + img2img</item>
/// <item>FLUX GGUF zatím skip — vrací chybu s návodem (potřebuje extra CLIP/T5/VAE)</item>
/// <item>1 obrázek, žádné LoRy, rozumné výchozí hodnoty</item>
/// </list>
///
/// <para>Pokud se uživatel chce hrát s modely/LoRy/parametry, pojde do Image Studia
/// kde má plnou kontrolu. V chatu je cíl one-shot UX bez zatěžování UI.</para>
/// </summary>
public sealed class ChatImageOrchestrator : IChatImageOrchestrator
{
    private readonly IImageIntentParser       _parser;
    private readonly IImageModelMatcher       _matcher;
    private readonly IImageModelRecommender   _recommender;
    private readonly IComfyService            _comfy;
    private readonly IImageRepository         _repo;
    private readonly ISettingsService         _settings;
    private readonly IDownloadService         _downloader;
    // Optional — FLUX.1 Kontext instrukční editace. Null = Kontext se přeskočí
    // (padá na klasický denoise img2img). Optional kvůli existujícím testům, které
    // orchestrátor konstruují bez něj.
    private readonly IKontextService?         _kontext;
    // Optional — PuLID-Flux identita osoby bez tréninku. Null = person-gen se neprovede.
    private readonly IPuLIDService?           _pulid;
    // Optional — ESRGAN upscale model pro „upscale ikonu". Null = upscale se neprovede.
    private readonly IUpscaleModelService?    _upscale;
    private readonly string?                  _outputDirOverride;
    private readonly string?                  _modelsDirOverride;

    public ChatImageOrchestrator(
        IImageIntentParser     parser,
        IImageModelMatcher     matcher,
        IImageModelRecommender recommender,
        IComfyService          comfy,
        IImageRepository       repo,
        ISettingsService       settings,
        IDownloadService       downloader,
        IKontextService?       kontext = null,
        IPuLIDService?         pulid   = null,
        IUpscaleModelService?  upscale = null)
        : this(parser, matcher, recommender, comfy, repo, settings, downloader,
               outputDirOverride: null, modelsDirOverride: null, kontext: kontext, pulid: pulid,
               upscale: upscale) { }

    /// <summary>
    /// Test-only overload — umožňuje přesměrovat výstupní složku mimo
    /// %AppData% (nechceme aby unit testy zaplňovaly uživatelskou galerii)
    /// a modelsDir pro download flow.
    /// </summary>
    internal ChatImageOrchestrator(
        IImageIntentParser     parser,
        IImageModelMatcher     matcher,
        IImageModelRecommender recommender,
        IComfyService          comfy,
        IImageRepository       repo,
        ISettingsService       settings,
        IDownloadService       downloader,
        string?                outputDirOverride,
        string?                modelsDirOverride,
        IKontextService?       kontext = null,
        IPuLIDService?         pulid   = null,
        IUpscaleModelService?  upscale = null)
    {
        _kontext           = kontext;
        _pulid             = pulid;
        _upscale           = upscale;
        _parser            = parser;
        _matcher           = matcher;
        _recommender       = recommender;
        _comfy             = comfy;
        _repo              = repo;
        _settings          = settings;
        _downloader        = downloader;
        _outputDirOverride = outputDirOverride;
        _modelsDirOverride = modelsDirOverride;
    }

    public async Task<ChatImageGenerationResult> GenerateAsync(
        string                                                            czechPrompt,
        string?                                                           referenceImagePath,
        IProgress<int>?                                                   progress,
        CancellationToken                                                 ct,
        Func<ModelUpgradeOffer, CancellationToken, Task<UpgradeChoice>>?  askForUpgrade    = null,
        IProgress<DownloadStatusUpdate>?                                  downloadProgress = null,
        IProgress<ChatImageGenStage>?                                     stageProgress    = null)
    {
        try
        {
            stageProgress?.Report(ChatImageGenStage.Recommending);

            // 1) Comfy musí běžet — uživatel může mít startup ještě v progresu
            if (!_comfy.IsRunning)
            {
                Log.Information("ChatImageOrchestrator: ComfyUI neběží, spouštím…");
                var started = await _comfy.StartAsync(ct);
                if (!started)
                    return Fail("ComfyUI se nepodařilo spustit — zkontroluj nastavení v Settings.");
            }

            // 2) Parse intent — vytáhne český → EN prompt + kind + aspect
            Log.Information("ChatImageOrchestrator: parsing intent pro: {Prompt}", Truncate(czechPrompt, 80));
            var intent = await _parser.ParseAsync(czechPrompt, ct);

            // 2.5) FLUX.1 Kontext — instrukční editace. Když máme referenci (uživatel
            //      přiložil fotku nebo edituje předchozí obrázek) a Kontext je k
            //      dispozici (nebo se ho podaří stáhnout), použijeme ho MÍSTO klasického
            //      denoise img2img — výsledek zachovává originál a aplikuje instrukci
            //      (blíž ChatGPT editaci). Bypassuje výběr checkpointu (Kontext má vlastní
            //      UNET). Když to nevyjde, padáme dál na klasický checkpoint img2img.
            if (!string.IsNullOrEmpty(referenceImagePath) && File.Exists(referenceImagePath))
            {
                var kontextResult = await TryKontextAsync(
                    referenceImagePath, intent, progress, downloadProgress, stageProgress, ct);
                if (kontextResult is not null)
                    return kontextResult;
            }

            // 3) Match model — z dostupných checkpointů vybereme nejvhodnější + zkusíme upgrade
            var available = await _comfy.GetCheckpointsAsync(ct);

            // 3a) Recommender — máme lepší model v katalogu, který uživatel nemá?
            //     Toto je nejpomalejší krok recommend fáze (live API call).
            var recommendation = await _recommender.RecommendAsync(intent, available, ct);
            var model          = recommendation.LocalBestMatch;

            if (recommendation.Upgrade is not null && askForUpgrade is not null)
            {
                Log.Information("ChatImageOrchestrator: nabízím upgrade na {Model} ({Size} MB)",
                                recommendation.Upgrade.Name, recommendation.Upgrade.SizeBytes / 1_048_576);

                var choice = await askForUpgrade(recommendation.Upgrade, ct);

                switch (choice)
                {
                    case UpgradeChoice.Cancel:
                        return Fail("Generování zrušeno uživatelem.");

                    case UpgradeChoice.DownloadBetter:
                        // Pre-flight disk check — předejít situaci kdy se začne
                        // stahovat 7 GB model a v polovině dojde místo na disku.
                        var diskCheck = CheckDiskSpace(recommendation.Upgrade.SizeBytes);
                        if (diskCheck is not null)
                            return Fail(diskCheck);

                        var downloaded = await TryDownloadUpgradeAsync(recommendation.Upgrade, downloadProgress, ct);
                        if (!downloaded)
                            return Fail($"Stažení modelu {recommendation.Upgrade.Name} selhalo — zkus jiný model nebo to opakuj později.");

                        // Refresh checkpointů — po download by tam měl být. Pokud Comfy
                        // model ještě nevidí (cache, indexer delay), padáme zpátky na původní lokální.
                        available = await _comfy.GetCheckpointsAsync(ct);
                        model     = available.FirstOrDefault(c =>
                                        c.Equals(recommendation.Upgrade.FileName, StringComparison.OrdinalIgnoreCase))
                                    ?? recommendation.LocalBestMatch;
                        if (model is null)
                            return Fail("Model byl stažen, ale ComfyUI ho ještě nevidí. Restartuj aplikaci a zkus to znovu.");
                        break;

                    case UpgradeChoice.UseLocal:
                    default:
                        // model = recommendation.LocalBestMatch (už nastaveno)
                        break;
                }
            }

            if (available.Count == 0)
                return Fail("V ComfyUI nejsou žádné checkpoint modely. Stáhni si nějaký v sekci Modely.");

            if (model is null)
                return Fail($"Nepodařilo se najít vhodný model pro {intent.Kind}. Stáhni si nějaký v sekci Modely.");

            // Recommend / upgrade fáze hotová — od teď je to čisté generování
            stageProgress?.Report(ChatImageGenStage.Generating);

            // 4) Sestav workflow — txt2img / img2img dle reference
            var (width, height) = AspectToResolution(intent.Aspect, ComfyWorkflowBuilder.IsFluxModel(model));
            var seed            = Random.Shared.NextInt64();
            var isFlux          = ComfyWorkflowBuilder.IsFluxModel(model);
            var isGguf          = ComfyWorkflowBuilder.IsGgufModel(model);

            if (isGguf)
                return Fail("FLUX GGUF v chatu zatím nepodporujeme — použij Image Studio (potřebuje CLIP/T5/VAE které nejdou autopickovat).");

            string? uploadedRef = null;
            if (!string.IsNullOrEmpty(referenceImagePath) && File.Exists(referenceImagePath))
            {
                try
                {
                    uploadedRef = await _comfy.UploadImageAsync(referenceImagePath, ct);
                    Log.Information("ChatImageOrchestrator: reference nahrána jako {Name}", uploadedRef);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "ChatImageOrchestrator: upload reference selhal, padáme na txt2img");
                    uploadedRef = null;
                }
            }

            var steps = isFlux ? ComfyWorkflowBuilder.FluxDefaults(model).Steps    : 25;
            var cfg   = isFlux ? ComfyWorkflowBuilder.FluxDefaults(model).Guidance : 7.0;
            var sampler   = isFlux ? ComfyWorkflowBuilder.DefaultSamplerFlux   : ComfyWorkflowBuilder.DefaultSamplerSd;
            var scheduler = isFlux ? ComfyWorkflowBuilder.DefaultSchedulerFlux : ComfyWorkflowBuilder.DefaultSchedulerSd;

            // Fallback img2img denoise — tohle běží jen když se NEpoužil Kontext
            // (ten je primární pro editaci). V chatu reference = editace, takže
            // nízký denoise (0.45) ZACHOVÁ vstupní fotku a jen ji posune dle promptu.
            // Dřív 0.78 = skoro regenerace z promptu → vznikal nesouvisející obrázek.
            const double EditDenoise = 0.45;
            var workflow = (isFlux, uploadedRef) switch
            {
                (true,  null)        => ComfyWorkflowBuilder.BuildFlux(model, intent.EnglishPrompt,
                                          width, height, steps, cfg, seed, 1, sampler, scheduler),
                (true,  string r)    => ComfyWorkflowBuilder.BuildFluxImg2Img(model, r, intent.EnglishPrompt,
                                          width, height, steps, cfg, seed, denoise: EditDenoise, 1, sampler, scheduler),
                (false, null)        => ComfyWorkflowBuilder.BuildStandard(model, intent.EnglishPrompt, intent.NegativePrompt,
                                          width, height, steps, cfg, seed, 1, sampler, scheduler),
                (false, string r)    => ComfyWorkflowBuilder.BuildStandardImg2Img(model, r, intent.EnglishPrompt, intent.NegativePrompt,
                                          width, height, steps, cfg, seed, denoise: EditDenoise, 1, sampler, scheduler),
            };

            // 5–7) Queue → wait → download → uložit do galerie (sdíleno s Kontext cestou)
            Log.Information("ChatImageOrchestrator: queuing prompt (model={Model}, {W}x{H}, steps={Steps})",
                            model, width, height, steps);
            return await QueueAndFinishAsync(
                workflow, intent, model, seed, width, height, steps, cfg, sampler, scheduler, progress, ct);
        }
        catch (OperationCanceledException)
        {
            Log.Information("ChatImageOrchestrator: zrušeno uživatelem");
            return Fail("Generování zrušeno.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ChatImageOrchestrator: neočekávaná chyba");
            return Fail($"Chyba: {ex.Message}");
        }
    }

    // ── PuLID — generování osoby z fotky obličeje (identita bez tréninku) ─────

    public async Task<ChatImageGenerationResult> GeneratePersonAsync(
        string                            czechPrompt,
        string                            faceImagePath,
        IProgress<int>?                   progress,
        CancellationToken                 ct,
        IProgress<DownloadStatusUpdate>?  downloadProgress = null,
        IProgress<ChatImageGenStage>?     stageProgress    = null)
    {
        try
        {
            if (_pulid is null)
                return Fail("PuLID není k dispozici.");
            if (string.IsNullOrEmpty(faceImagePath) || !File.Exists(faceImagePath))
                return Fail("Referenční fotka obličeje nebyla nalezena.");

            stageProgress?.Report(ChatImageGenStage.Recommending);

            // 1) ComfyUI běží
            if (!_comfy.IsRunning && !await _comfy.StartAsync(ct))
                return Fail("ComfyUI se nepodařilo spustit.");

            // 2) PuLID stack — auto-install při prvním použití (node + deps + model + antelopev2)
            if (!_pulid.IsAvailable())
            {
                Log.Information("ChatImageOrchestrator: PuLID se instaluje (první použití)");
                var dl = new Progress<DownloadProgressInfo>(info =>
                {
                    var pct = info.Total > 0 ? (int)(100 * info.Downloaded / info.Total) : 0;
                    downloadProgress?.Report(new DownloadStatusUpdate(
                        "PuLID", pct, info.Downloaded, info.Total, info.BytesPerSecond / 1_048_576.0));
                });
                await _pulid.EnsureAsync(dl, ct);
                if (!_pulid.IsAvailable())
                    return Fail("PuLID se nepodařilo nainstalovat — zkus to znovu (viz log).");
            }

            // 3) FLUX UNET — preferuj plain flux1-dev, fallback na cokoliv flux (i kontext).
            var modelsDir = ResolveModelsDirectory();
            var unetFile  = FindFluxUnet(modelsDir);
            if (unetFile is null)
                return Fail("Nenašel jsem FLUX UNET model pro PuLID. Stáhni FLUX.1 dev.");

            // 4) Parse intent — český → EN prompt + aspekt
            var intent = await _parser.ParseAsync(czechPrompt, ct);

            // 5) Upload reference + sestav PuLID workflow
            stageProgress?.Report(ChatImageGenStage.Generating);
            string uploadedFace;
            try { uploadedFace = await _comfy.UploadImageAsync(faceImagePath, ct); }
            catch (Exception ex) { Log.Warning(ex, "PuLID: upload reference selhal"); return Fail("Nahrání fotky do ComfyUI selhalo."); }

            var (width, height) = AspectToResolution(intent.Aspect, isFlux: true);
            var (steps, guidance) = ComfyWorkflowBuilder.FluxDefaults(unetFile);
            var seed = Random.Shared.NextInt64();

            var workflow = ComfyWorkflowBuilder.BuildFluxPuLID(
                unetFile,
                ComfyWorkflowBuilder.DefaultFluxClipL,
                ComfyWorkflowBuilder.DefaultFluxT5,
                ComfyWorkflowBuilder.DefaultFluxVae,
                _pulid.PulidModelFileName,
                uploadedFace, intent.EnglishPrompt,
                width, height, steps, guidance, seed);

            Log.Information("ChatImageOrchestrator: PuLID generování (UNET={Unet}, {W}x{H})", unetFile, width, height);

            return await QueueAndFinishAsync(
                workflow, intent, "PuLID-Flux", seed, width, height, steps, 1.0,
                ComfyWorkflowBuilder.DefaultSamplerFlux, ComfyWorkflowBuilder.DefaultSchedulerFlux,
                progress, ct);
        }
        catch (OperationCanceledException)
        {
            return Fail("Generování zrušeno.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ChatImageOrchestrator: PuLID generování selhalo");
            return Fail($"Chyba: {ex.Message}");
        }
    }

    // ── Upscale hotového obrázku (ESRGAN) ────────────────────────────────────

    public async Task<ChatImageGenerationResult> UpscaleImageAsync(
        string                            imagePath,
        IProgress<int>?                   progress,
        CancellationToken                 ct,
        IProgress<DownloadStatusUpdate>?  downloadProgress = null)
    {
        try
        {
            if (_upscale is null)
                return Fail("Upscale služba není k dispozici.");
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                return Fail("Obrázek pro upscale nebyl nalezen.");

            // 1) ComfyUI běží
            if (!_comfy.IsRunning && !await _comfy.StartAsync(ct))
                return Fail("ComfyUI se nepodařilo spustit.");

            // 2) ESRGAN model — auto-download při prvním použití
            var modelsDir = ResolveModelsDirectory();
            if (!_upscale.IsAvailable(modelsDir))
            {
                var dl = new Progress<DownloadProgressInfo>(info =>
                {
                    var pct = info.Total > 0 ? (int)(100 * info.Downloaded / info.Total) : 0;
                    downloadProgress?.Report(new DownloadStatusUpdate(
                        "Upscale model", pct, info.Downloaded, info.Total, info.BytesPerSecond / 1_048_576.0));
                });
                await _upscale.EnsureAsync(modelsDir, dl, ct);
                if (!_upscale.IsAvailable(modelsDir))
                    return Fail("Upscale model se nepodařilo stáhnout — zkus to znovu.");
            }

            // 3) Upload + workflow + dokončení (sdílí QueueAndFinishAsync)
            string uploaded;
            try { uploaded = await _comfy.UploadImageAsync(imagePath, ct); }
            catch (Exception ex) { Log.Warning(ex, "Upscale: upload selhal"); return Fail("Nahrání obrázku do ComfyUI selhalo."); }

            var workflow = ComfyWorkflowBuilder.BuildImageUpscale(uploaded, _upscale.ModelFileName);

            // Syntetický intent — upscale nemá prompt; jen popis pro metadata/galerii.
            var intent = new ImageIntent(
                ImageKind.Auto, ImageAspect.Square, ImageQualityHint.HighRes,
                EnglishPrompt: "upscale (ESRGAN x2)",
                NegativePrompt: "",
                Reasoning: "Upscale 2×");

            Log.Information("ChatImageOrchestrator: upscale {Path}", imagePath);
            return await QueueAndFinishAsync(
                workflow, intent, "Upscale (ESRGAN ×2)", seed: 0, width: 0, height: 0,
                steps: 0, cfg: 0, ComfyWorkflowBuilder.DefaultSamplerSd, ComfyWorkflowBuilder.DefaultSchedulerSd,
                progress, ct);
        }
        catch (OperationCanceledException) { return Fail("Upscale zrušen."); }
        catch (Exception ex)
        {
            Log.Error(ex, "ChatImageOrchestrator: upscale selhal");
            return Fail($"Chyba: {ex.Message}");
        }
    }

    /// <summary>Najde FLUX UNET v Models (unet/ i root). Preferuje plain flux1-dev před kontext variantou.</summary>
    private static string? FindFluxUnet(string modelsDir)
    {
        try
        {
            var unetDir = Path.Combine(modelsDir, "unet");
            var scanDir = Directory.Exists(unetDir) ? unetDir : modelsDir;
            var files = Directory
                .EnumerateFiles(scanDir, "*.safetensors", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f).Contains("flux", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (files.Count == 0) return null;

            var plain = files.FirstOrDefault(f =>
                !Path.GetFileName(f).Contains("kontext", StringComparison.OrdinalIgnoreCase));
            return Path.GetFileName(plain ?? files[0]);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FindFluxUnet: sken {Dir} selhal", modelsDir);
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sdílený konec generování: zařadí workflow do ComfyUI, počká na výsledek,
    /// stáhne obrázek na disk a zapíše ho do galerie. Používá ho jak klasická
    /// txt2img/img2img cesta, tak Kontext editace.
    /// </summary>
    private async Task<ChatImageGenerationResult> QueueAndFinishAsync(
        Dictionary<string, object> workflow,
        ImageIntent       intent,
        string            modelLabel,
        long              seed,
        int               width,
        int               height,
        int               steps,
        double            cfg,
        string            sampler,
        string            scheduler,
        IProgress<int>?   progress,
        CancellationToken ct)
    {
        var promptId = await _comfy.QueuePromptAsync(workflow, ct);

        var result = await _comfy.WaitForResultAsync(promptId, progress, ct);

        // Po dokončení generování uvolni modely z VRAM — chat sdílí GPU s LLM
        // (FLUX ~12 GB + 24B LLM se na 24 GB najednou nevejdou). Obrázek je už
        // v ComfyUI output složce, takže download po unloadu nic neztratí.
        // CancellationToken.None: uvolnit chceme i když uživatel generování zrušil.
        await _comfy.FreeMemoryAsync(CancellationToken.None);

        if (result is null || result.Images.Count == 0)
            return Fail("ComfyUI nevrátil žádný obrázek (zrušeno nebo timeout).");

        var imgRef    = result.Images[0];
        var bytes     = await _comfy.DownloadImageAsync(imgRef.Filename, imgRef.Subfolder, imgRef.Type, ct);
        // Vepiš AI-provenience do PNG metadat (že je AI, jaký model, jaká aplikace).
        bytes         = PngTextMetadata.AddAiProvenance(bytes, modelLabel, intent.EnglishPrompt);
        var outputDir = GetOutputDirectory();
        Directory.CreateDirectory(outputDir);
        var fileName  = $"AIStudio_chat_{DateTime.Now:yyyyMMdd_HHmmss}_{imgRef.Filename}";
        var filePath  = Path.Combine(outputDir, fileName);
        await File.WriteAllBytesAsync(filePath, bytes, ct);

        var id = Guid.NewGuid().ToString();
        var record = new ImageRecord(
            Id:          id,
            FilePath:    filePath,
            Prompt:      intent.EnglishPrompt,
            ModelName:   modelLabel,
            Seed:        seed,
            Width:       width,
            Height:      height,
            Steps:       steps,
            Cfg:         cfg,
            Sampler:     sampler,
            Scheduler:   scheduler,
            GeneratedAt: DateTime.Now);

        try { await _repo.SaveImageAsync(record); }
        catch (Exception ex) { Log.Warning(ex, "ChatImageOrchestrator: SaveImageAsync selhalo — soubor je na disku, jen není v galerii"); }

        Log.Information("ChatImageOrchestrator: hotovo → {Path}", filePath);
        return new ChatImageGenerationResult(
            Success:       true,
            ImagePath:     filePath,
            ImageId:       id,
            ModelUsed:     modelLabel,
            EnglishPrompt: intent.EnglishPrompt,
            Reasoning:     intent.Reasoning,
            Seed:          (int)(seed & int.MaxValue),
            Width:         width,
            Height:        height);
    }

    /// <summary>
    /// Zkusí editaci přes FLUX.1 Kontext. Vrátí výsledek při úspěchu, nebo
    /// <c>null</c> pokud Kontext není k dispozici / nepodaří se stáhnout / cokoliv
    /// selže — pak volající padá zpět na klasický denoise img2img.
    ///
    /// <para>Když Kontext není stažený, pokusí se ho stáhnout (uživatel zvolil
    /// „vše automaticky"). První edit tak může chvíli trvat (~12 GB), další jsou
    /// okamžité. Progress jde přes <paramref name="downloadProgress"/>.</para>
    /// </summary>
    private async Task<ChatImageGenerationResult?> TryKontextAsync(
        string                            referenceImagePath,
        ImageIntent                       intent,
        IProgress<int>?                   progress,
        IProgress<DownloadStatusUpdate>?  downloadProgress,
        IProgress<ChatImageGenStage>?     stageProgress,
        CancellationToken                 ct)
    {
        if (_kontext is null) return null;

        var modelsDir = ResolveModelsDirectory();

        if (!_kontext.IsAvailable(modelsDir))
        {
            Log.Information("ChatImageOrchestrator: Kontext není stažený, zkouším auto-download");
            try
            {
                var dl = new Progress<DownloadProgressInfo>(info =>
                {
                    var pct = info.Total > 0 ? (int)(100 * info.Downloaded / info.Total) : 0;
                    downloadProgress?.Report(new DownloadStatusUpdate(
                        "FLUX.1 Kontext", pct, info.Downloaded, info.Total,
                        MegabytesPerSecond: info.BytesPerSecond / 1_048_576.0));
                });
                await _kontext.EnsureAsync(modelsDir, _settings.Settings.HuggingFaceToken, dl, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warning(ex, "ChatImageOrchestrator: Kontext auto-download selhal, padám na klasický img2img");
                return null;
            }

            if (!_kontext.IsAvailable(modelsDir))
            {
                Log.Information("ChatImageOrchestrator: Kontext stále nedostupný po download pokusu, padám na img2img");
                return null;
            }
        }

        // Reference nahrajeme do ComfyUI a postavíme Kontext workflow
        string uploadedRef;
        try
        {
            uploadedRef = await _comfy.UploadImageAsync(referenceImagePath, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ChatImageOrchestrator: upload reference pro Kontext selhal, padám na img2img");
            return null;
        }

        stageProgress?.Report(ChatImageGenStage.Generating);

        var (steps, guidance) = ComfyWorkflowBuilder.KontextDefaults;
        var seed     = Random.Shared.NextInt64();
        var workflow = ComfyWorkflowBuilder.BuildFluxKontext(
            _kontext.UnetFileName,
            ComfyWorkflowBuilder.DefaultFluxClipL,
            ComfyWorkflowBuilder.DefaultFluxT5,
            ComfyWorkflowBuilder.DefaultFluxVae,
            uploadedRef, intent.EnglishPrompt, steps, guidance, seed);

        Log.Information("ChatImageOrchestrator: Kontext editace (UNET={Unet})", _kontext.UnetFileName);

        // Rozměry výstupu řeší FluxKontextImageScale za běhu — pro metadata galerie
        // je předem neznáme, dáme 0 (uživatelsky nepodstatné).
        return await QueueAndFinishAsync(
            workflow, intent, "FLUX.1 Kontext",
            seed, width: 0, height: 0, steps: steps, cfg: 1.0,
            sampler:   ComfyWorkflowBuilder.DefaultSamplerFlux,
            scheduler: ComfyWorkflowBuilder.DefaultSchedulerFlux,
            progress, ct);
    }

    /// <summary>
    /// Mapuje aspect na konkrétní rozlišení. SD/SDXL = 1024×*, FLUX = 1024×*
    /// (FLUX zvládá i 1024 nativně, vyšší rozlišení by chtělo víc VRAM).
    /// </summary>
    private static (int W, int H) AspectToResolution(ImageAspect aspect, bool isFlux)
    {
        var baseSize = isFlux ? 1024 : 1024;
        return aspect switch
        {
            ImageAspect.Landscape => (1216, 832),  // ~3:2
            ImageAspect.Portrait  => (832,  1216), // ~2:3
            _                     => (baseSize, baseSize),
        };
    }

    /// <summary>
    /// Cesta pro vygenerované obrázky. Sdílíme se s Image Studiem
    /// (<c>%AppData%/AIStudio/Images/</c>), aby se obrázky z chatu objevily
    /// v galerii bez kopírování.
    /// </summary>
    private string GetOutputDirectory()
    {
        if (!string.IsNullOrEmpty(_outputDirOverride)) return _outputDirOverride;
        // Settings.ImagesDirectory by se hodilo, ale zatím neexistuje (chat
        // obrázky se ukládají vedle ImageStudio obrázků).
        return AppPaths.DefaultImagesDirectory;
    }

    /// <summary>
    /// Resolve modelů adresáře — preferuje test override → user settings → default.
    /// Sdílená logika pro CheckDiskSpace a TryDownloadUpgradeAsync.
    /// </summary>
    private string ResolveModelsDirectory() =>
        !string.IsNullOrEmpty(_modelsDirOverride)
            ? _modelsDirOverride
            : AppPaths.ResolveModelsDirectory(_settings.Settings.ModelsDirectory);

    /// <summary>
    /// Ověří, jestli na disku zbývá dost místa pro stažení modelu velikosti
    /// <paramref name="requiredBytes"/> + 500 MB safety margin.
    /// Vrátí null pokud OK, jinak user-friendly error message pro UI.
    ///
    /// <para>Defenzivní — pokud disk info nejde získat (UNC path, dropbox, …),
    /// vrátíme null a důvěřujeme, že download projde.</para>
    /// </summary>
    private string? CheckDiskSpace(long requiredBytes)
    {
        try
        {
            var modelsDir = ResolveModelsDirectory();

            // GetPathRoot vrátí "C:\" nebo "/" — DriveInfo to akceptuje
            var root = Path.GetPathRoot(Path.GetFullPath(modelsDir));
            if (string.IsNullOrEmpty(root)) return null;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return null;

            const long SafetyMargin = 500L * 1024 * 1024;  // 500 MB rezerva pro tmp + checksum
            var available = drive.AvailableFreeSpace;
            var needed    = requiredBytes + SafetyMargin;

            if (available < needed)
            {
                var availableGb = available / 1_073_741_824.0;
                var neededGb    = needed    / 1_073_741_824.0;
                Log.Warning("ChatImageOrchestrator: disk full check failed — need {Need:F1} GB, available {Avail:F1} GB on {Drive}",
                            neededGb, availableGb, drive.Name);
                return $"Nedostatek místa na disku {drive.Name} — potřeba {neededGb:F1} GB, " +
                       $"k dispozici {availableGb:F1} GB. Uvolni místo nebo přesměruj Models složku v Nastavení.";
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ChatImageOrchestrator: disk space check selhal — pokračujeme bez kontroly");
            return null;
        }
    }

    /// <summary>
    /// Stáhne nabídnutý upgrade model do Models složky. Vrátí true pokud OK.
    /// Nikdy nehází — chyba se zaloguje + vrátí false (orchestrátor pak
    /// uživateli ukáže "stažení selhalo, zkus to znovu").
    /// </summary>
    private async Task<bool> TryDownloadUpgradeAsync(
        ModelUpgradeOffer                 offer,
        IProgress<DownloadStatusUpdate>?  downloadProgress,
        CancellationToken                 ct)
    {
        try
        {
            var modelsDir = ResolveModelsDirectory();
            Directory.CreateDirectory(modelsDir);
            var destPath = Path.Combine(modelsDir, offer.FileName);

            // Pokud už soubor existuje (uživatel ho mezi tím stáhl jinde), skip
            if (File.Exists(destPath))
            {
                Log.Information("ChatImageOrchestrator: model {File} už existuje, přeskakuji download", offer.FileName);
                return true;
            }

            var apiToken = offer.RequiresHuggingFaceToken
                ? _settings.Settings.HuggingFaceToken
                : null;

            // DownloadProgressInfo (bytes-based) → DownloadStatusUpdate (percent + speed)
            var progressBridge = downloadProgress is null
                ? null
                : new Progress<DownloadProgressInfo>(p =>
                  {
                      downloadProgress.Report(new DownloadStatusUpdate(
                          ModelName:          offer.Name,
                          Percent:            (int)p.Percent,
                          BytesDone:          p.Downloaded,
                          BytesTotal:         p.Total,
                          MegabytesPerSecond: p.BytesPerSecond / 1_048_576.0));
                  });

            Log.Information("ChatImageOrchestrator: stahuji {Name} → {Dest} ({Size} MB)",
                            offer.Name, destPath, offer.SizeBytes / 1_048_576);

            await _downloader.DownloadFileAsync(
                url:            offer.DownloadUrl,
                destPath:       destPath,
                progress:       progressBridge,
                apiToken:       apiToken,
                ct:             ct,
                expectedSha256: offer.Sha256);

            Log.Information("ChatImageOrchestrator: stažení {Name} hotovo", offer.Name);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;  // propagujeme, vnější handler v GenerateAsync to chytne jako "Zrušeno"
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ChatImageOrchestrator: stažení upgrade modelu {Name} selhalo", offer.Name);
            return false;
        }
    }

    private static ChatImageGenerationResult Fail(string msg) =>
        new(Success: false, ErrorMessage: msg);

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
