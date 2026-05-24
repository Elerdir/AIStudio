using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

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
    private readonly IImageIntentParser  _parser;
    private readonly IImageModelMatcher  _matcher;
    private readonly IComfyService       _comfy;
    private readonly IImageRepository    _repo;
    private readonly ISettingsService    _settings;

    public ChatImageOrchestrator(
        IImageIntentParser parser,
        IImageModelMatcher matcher,
        IComfyService      comfy,
        IImageRepository   repo,
        ISettingsService   settings)
    {
        _parser   = parser;
        _matcher  = matcher;
        _comfy    = comfy;
        _repo     = repo;
        _settings = settings;
    }

    public async Task<ChatImageGenerationResult> GenerateAsync(
        string             czechPrompt,
        string?            referenceImagePath,
        IProgress<int>?    progress,
        CancellationToken  ct)
    {
        try
        {
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

            // 3) Match model — z dostupných checkpointů vybereme nejvhodnější
            var available = await _comfy.GetCheckpointsAsync(ct);
            if (available.Count == 0)
                return Fail("V ComfyUI nejsou žádné checkpoint modely. Stáhni si nějaký v sekci Modely.");

            var model = _matcher.Match(intent.Kind, available);
            if (model is null)
                return Fail($"Nepodařilo se najít vhodný model pro {intent.Kind}. Stáhni si nějaký v sekci Modely.");

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

            var workflow = (isFlux, uploadedRef) switch
            {
                (true,  null)        => ComfyWorkflowBuilder.BuildFlux(model, intent.EnglishPrompt,
                                          width, height, steps, cfg, seed, 1, sampler, scheduler),
                (true,  string r)    => ComfyWorkflowBuilder.BuildFluxImg2Img(model, r, intent.EnglishPrompt,
                                          width, height, steps, cfg, seed, denoise: 0.78, 1, sampler, scheduler),
                (false, null)        => ComfyWorkflowBuilder.BuildStandard(model, intent.EnglishPrompt, intent.NegativePrompt,
                                          width, height, steps, cfg, seed, 1, sampler, scheduler),
                (false, string r)    => ComfyWorkflowBuilder.BuildStandardImg2Img(model, r, intent.EnglishPrompt, intent.NegativePrompt,
                                          width, height, steps, cfg, seed, denoise: 0.78, 1, sampler, scheduler),
            };

            // 5) Queue + wait
            Log.Information("ChatImageOrchestrator: queuing prompt (model={Model}, {W}x{H}, steps={Steps})",
                            model, width, height, steps);
            var promptId = await _comfy.QueuePromptAsync(workflow, ct);

            var result = await _comfy.WaitForResultAsync(promptId, progress, ct);
            if (result is null || result.Images.Count == 0)
                return Fail("ComfyUI nevrátil žádný obrázek (zrušeno nebo timeout).");

            // 6) Download první obrázek a uložit na disk
            var imgRef    = result.Images[0];
            var bytes     = await _comfy.DownloadImageAsync(imgRef.Filename, imgRef.Subfolder, imgRef.Type, ct);
            var outputDir = GetOutputDirectory();
            Directory.CreateDirectory(outputDir);
            var fileName  = $"AIStudio_chat_{DateTime.Now:yyyyMMdd_HHmmss}_{imgRef.Filename}";
            var filePath  = Path.Combine(outputDir, fileName);
            await File.WriteAllBytesAsync(filePath, bytes, ct);

            // 7) Persist do SQLite galerie — uživatel ho najde i v Image Studiu
            var id = Guid.NewGuid().ToString();
            var record = new ImageRecord(
                Id:          id,
                FilePath:    filePath,
                Prompt:      intent.EnglishPrompt,
                ModelName:   model,
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
                ModelUsed:     model,
                EnglishPrompt: intent.EnglishPrompt,
                Reasoning:     intent.Reasoning,
                Seed:          (int)(seed & int.MaxValue),
                Width:         width,
                Height:        height);
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

    // ── Helpers ───────────────────────────────────────────────────────────────

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
        // Použijeme stejnou konvenci jako ImageGeneratorViewModel — pevně v %AppData%.
        // Settings.ImagesDirectory by se hodilo, ale zatím neexistuje (chat
        // obrázky se ukládají vedle ImageStudio obrázků).
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIStudio", "Images");
    }

    private static ChatImageGenerationResult Fail(string msg) =>
        new(Success: false, ErrorMessage: msg);

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
