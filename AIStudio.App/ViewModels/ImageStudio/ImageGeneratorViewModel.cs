using System.Collections.ObjectModel;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;

namespace AIStudio.App.ViewModels.ImageStudio;

public enum AspectRatio { R1x1, R16x9, R9x16, R4x3, R3x4, R21x9 }
public enum ImageQuality { SD, FHD, QHD, UHD4K }

public partial class ImageGeneratorViewModel : ViewModelBase
{
    private readonly IComfyService        _comfy;
    private readonly ISettingsService     _settings;
    private readonly IImageRepository     _imageRepo;
    private readonly IImageIntentParser   _intentParser;
    private readonly IImageModelMatcher   _modelMatcher;
    private readonly ILlamaService        _llama;
    private static int _counter;

    private CancellationTokenSource? _genCts;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _prompt = string.Empty;
    [ObservableProperty] private string _negativePrompt = string.Empty;
    [ObservableProperty] private string _selectedModel = "FLUX.1 Schnell";
    [ObservableProperty] private int _steps = 4;
    [ObservableProperty] private double _cfg = 1.0;
    [ObservableProperty] private long _seed = -1;
    [ObservableProperty] private int _variantCount = 1;
    [ObservableProperty] private AspectRatio _selectedAspectRatio = AspectRatio.R1x1;
    [ObservableProperty] private ImageQuality _selectedQuality = ImageQuality.FHD;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private int _generationProgress;
    [ObservableProperty] private string _generationStatus = string.Empty;
    [ObservableProperty] private string? _referenceImagePath;     // legacy: cesta prvního obrázku (kompatibilita)
    [ObservableProperty] private double _referenceStrength = 0.7;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReferenceImages))]
    private bool _hasReferenceImage;

    /// <summary>
    /// Seznam referenčních obrázků. Aktuální workflow používá první (kvůli zpětné
    /// kompatibilitě), ale UI umožňuje přidat víc — pro budoucí ControlNet stack /
    /// IP-Adapter multi-image / image-to-image kompozice.
    /// </summary>
    public ObservableCollection<ReferenceImageItem> ReferenceImages { get; } = new();

    public bool HasReferenceImages => ReferenceImages.Count > 0;
    [ObservableProperty] private GeneratedImageViewModel? _latestImage;

    // ── Smart mode (intent-driven generování) ────────────────────────────────

    /// <summary>
    /// Smart mode = jeden velký textový popis, parser → výběr modelu → generování.
    /// Manual mode = klasický form (model picker, prompt, neg, aspect, quality).
    /// Defaultně Smart, protože je to bližší ChatGPT/Gemini chování — uživatel
    /// má nižší práh a power-user může přepnout.
    /// </summary>
    [ObservableProperty] private bool _isSmartMode = true;

    /// <summary>Surový popis v češtině/angličtině — vstup pro intent parser.</summary>
    [ObservableProperty] private string _smartPrompt = string.Empty;

    /// <summary>True dokud parser pracuje (LLM volání před samotným generováním).</summary>
    [ObservableProperty] private bool _isParsingIntent;

    /// <summary>
    /// Vysvětlení co Smart mode vybral — uživatel vidí „Vybral jsem epicrealism, …"
    /// pod input polem. Drží se i po dokončení generování, dokud uživatel nepošle
    /// novou žádost — kvůli transparenci a možnosti override v Manual módu.
    /// </summary>
    [ObservableProperty] private string _smartReasoning = string.Empty;

    // Available checkpoints fetched from ComfyUI
    [ObservableProperty] private ObservableCollection<string> _availableCheckpoints = new();

    public ObservableCollection<GeneratedImageViewModel> GeneratedImages { get; } = new();

    public static IReadOnlyList<AspectRatio> AspectRatios { get; } = Enum.GetValues<AspectRatio>();
    public static IReadOnlyList<ImageQuality> Qualities { get; }   = Enum.GetValues<ImageQuality>();

    public string AspectRatioLabel => SelectedAspectRatio switch
    {
        AspectRatio.R1x1  => "1:1",
        AspectRatio.R16x9 => "16:9",
        AspectRatio.R9x16 => "9:16",
        AspectRatio.R4x3  => "4:3",
        AspectRatio.R3x4  => "3:4",
        AspectRatio.R21x9 => "21:9",
        _                 => "1:1"
    };

    public string QualityLabel => SelectedQuality switch
    {
        ImageQuality.SD    => "SD  (512)",
        ImageQuality.FHD   => "FHD (1024)",
        ImageQuality.QHD   => "2K  (1536)",
        ImageQuality.UHD4K => "4K  (2048)",
        _                  => "FHD"
    };

    public (int W, int H) Resolution => (SelectedAspectRatio, SelectedQuality) switch
    {
        (AspectRatio.R16x9, ImageQuality.SD)    => (512,  288),
        (AspectRatio.R16x9, ImageQuality.FHD)   => (1024, 576),
        (AspectRatio.R16x9, ImageQuality.QHD)   => (1344, 768),
        (AspectRatio.R16x9, ImageQuality.UHD4K) => (1920, 1080),
        (AspectRatio.R9x16, ImageQuality.SD)    => (288,  512),
        (AspectRatio.R9x16, ImageQuality.FHD)   => (576,  1024),
        (AspectRatio.R9x16, ImageQuality.QHD)   => (768,  1344),
        (AspectRatio.R4x3,  ImageQuality.FHD)   => (1024, 768),
        (AspectRatio.R4x3,  ImageQuality.QHD)   => (1360, 1024),
        (AspectRatio.R3x4,  ImageQuality.FHD)   => (768,  1024),
        (AspectRatio.R3x4,  ImageQuality.QHD)   => (1024, 1360),
        (AspectRatio.R21x9, ImageQuality.FHD)   => (1024, 440),
        (AspectRatio.R21x9, ImageQuality.QHD)   => (1536, 660),
        (AspectRatio.R1x1,  ImageQuality.SD)    => (512,  512),
        (AspectRatio.R1x1,  ImageQuality.FHD)   => (1024, 1024),
        (AspectRatio.R1x1,  ImageQuality.QHD)   => (1536, 1536),
        (AspectRatio.R1x1,  ImageQuality.UHD4K) => (2048, 2048),
        _                                        => (1024, 1024)
    };

    public string ResolutionLabel
    {
        get { var r = Resolution; return $"{r.W} × {r.H}"; }
    }

    /// <summary>True když je vybraný čtvercový poměr — pro aktivní stav ikony ve Smart UI.</summary>
    public bool IsAspectSquare    => SelectedAspectRatio == AspectRatio.R1x1;
    /// <summary>True pro 16:9 / 4:3 / 21:9 (širokoúhlé).</summary>
    public bool IsAspectLandscape => SelectedAspectRatio is AspectRatio.R16x9
                                                          or AspectRatio.R4x3
                                                          or AspectRatio.R21x9;
    /// <summary>True pro 9:16 / 3:4 (na výšku).</summary>
    public bool IsAspectPortrait  => SelectedAspectRatio is AspectRatio.R9x16
                                                          or AspectRatio.R3x4;

    public bool HasGeneratedImages => GeneratedImages.Count > 0;

    public ImageGeneratorViewModel(
        IComfyService        comfy,
        ISettingsService     settings,
        IImageRepository     imageRepo,
        IImageIntentParser   intentParser,
        IImageModelMatcher   modelMatcher,
        ILlamaService        llama)
    {
        _comfy        = comfy;
        _settings     = settings;
        _imageRepo    = imageRepo;
        _intentParser = intentParser;
        _modelMatcher = modelMatcher;
        _llama        = llama;
        var n = System.Threading.Interlocked.Increment(ref _counter);
        _title = $"Generátor {n}";

        UpdateModelDefaults(SelectedModel);
    }

    // ── Property hooks ────────────────────────────────────────────────────────

    partial void OnSelectedAspectRatioChanged(AspectRatio value)
    {
        OnPropertyChanged(nameof(AspectRatioLabel));
        OnPropertyChanged(nameof(ResolutionLabel));
        OnPropertyChanged(nameof(IsAspectSquare));
        OnPropertyChanged(nameof(IsAspectLandscape));
        OnPropertyChanged(nameof(IsAspectPortrait));
    }

    partial void OnSelectedQualityChanged(ImageQuality value)
    {
        OnPropertyChanged(nameof(QualityLabel));
        OnPropertyChanged(nameof(ResolutionLabel));
    }

    partial void OnSelectedModelChanged(string value) => UpdateModelDefaults(value);

    private void UpdateModelDefaults(string model)
    {
        if (ComfyWorkflowBuilder.IsFluxModel(model))
        {
            var (steps, guidance) = ComfyWorkflowBuilder.FluxDefaults(model);
            Steps = steps;
            Cfg   = guidance;
        }
        else
        {
            Steps = 20;
            Cfg   = 7.0;
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task LoadCheckpointsAsync()
    {
        // Sloučíme dva zdroje:
        //   1) Běžící ComfyUI (autoritativní — to, co umí skutečně načíst)
        //   2) Lokální Models/ adresář (aby uživatel viděl stažené modely
        //      i když ComfyUI ještě neběží; pomáhá to potvrdit, že stahování
        //      přes Models proběhlo. Generování sice ještě selže na chybějícím
        //      ComfyUI, ale aspoň je picker užitečný).
        var combined = new List<string>();

        if (_comfy.IsRunning)
        {
            try
            {
                var fromComfy = await _comfy.GetCheckpointsAsync();
                combined.AddRange(fromComfy);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ImageGenerator: GetCheckpointsAsync failed, using local scan only");
            }
        }

        foreach (var name in ScanLocalImageModels())
            if (!combined.Contains(name, StringComparer.OrdinalIgnoreCase))
                combined.Add(name);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AvailableCheckpoints.Clear();
            foreach (var cp in combined)
                AvailableCheckpoints.Add(cp);

            // Pokud byl smazán právě zvolený checkpoint, přepneme se na první
            // dostupný. Bez toho by SelectedModel dál ukazoval na neexistující
            // soubor a další pokus o generování by selhal validation errorem.
            if (!AvailableCheckpoints.Contains(SelectedModel))
            {
                SelectedModel = AvailableCheckpoints.FirstOrDefault() ?? string.Empty;
            }
        });
    }

    /// <summary>
    /// Najde v Models/ složce všechny soubory, které vypadají jako image checkpointy.
    /// .safetensors je přímo SDXL/SD; .gguf jen pokud je to FLUX (chat GGUFy
    /// odfiltrujeme heuristikou na název souboru).
    /// </summary>
    private List<string> ScanLocalImageModels()
    {
        var customDir = _settings.Settings.ModelsDirectory;
        var dir = string.IsNullOrWhiteSpace(customDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                           "AIStudio", "Models")
            : customDir;

        if (!Directory.Exists(dir)) return new List<string>();

        var found = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.safetensors", SearchOption.AllDirectories))
                found.Add(Path.GetFileName(path));

            // Image GGUF jsou typicky pojmenované flux1-* nebo sd-*
            foreach (var path in Directory.EnumerateFiles(dir, "*.gguf", SearchOption.AllDirectories))
            {
                var fn = Path.GetFileName(path);
                if (fn.StartsWith("flux", StringComparison.OrdinalIgnoreCase) ||
                    fn.StartsWith("sd",   StringComparison.OrdinalIgnoreCase))
                    found.Add(fn);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ImageGenerator: scan {Dir} failed", dir);
        }

        return found;
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (!_comfy.IsRunning)
        {
            GenerationStatus = "ComfyUI není spuštěno";
            return;
        }

        if (string.IsNullOrWhiteSpace(Prompt))
        {
            GenerationStatus = "Zadej prompt";
            return;
        }

        using var cts = new CancellationTokenSource();
        _genCts = cts;
        IsGenerating       = true;
        GenerationProgress = 0;
        GenerationStatus   = "Odesílám do fronty…";

        try
        {
            var res    = Resolution;
            var seed   = Seed < 0 ? (long)Random.Shared.Next(1, int.MaxValue) : Seed;
            var isFlux = ComfyWorkflowBuilder.IsFluxModel(SelectedModel);
            var isGguf = ComfyWorkflowBuilder.IsGgufModel(SelectedModel);

            // Pre-flight check: FLUX GGUF potřebuje samostatné CLIP-L + T5 + VAE
            // soubory v Models složce. Když chybí, ušetříme kruhovou cestu přes
            // ComfyUI validation error a uživateli rovnou řekneme, co stáhnout.
            if (isFlux && isGguf)
            {
                var missing = FindMissingFluxDependencies();
                if (missing.Count > 0)
                {
                    GenerationStatus =
                        $"Chybí FLUX závislosti: {string.Join(", ", missing)}. " +
                        "Stáhni je v sekci Modely — položky s označením povinné.";
                    return;
                }
            }

            // Reference images: nahrajeme všechny lokální soubory do ComfyUI input/
            // (pre-flight, ještě před workflow buildem — kdyby upload selhal, ohlásíme
            // to dřív, než pošleme generovací request).
            List<string> uploadedRefNames = new();
            if (HasReferenceImages)
            {
                GenerationStatus = $"Nahrávám {ReferenceImages.Count} referenčních obrázků…";
                try
                {
                    foreach (var r in ReferenceImages)
                    {
                        var name = await _comfy.UploadImageAsync(r.Path, cts.Token);
                        uploadedRefNames.Add(name);
                    }
                }
                catch (Exception ex)
                {
                    GenerationStatus = $"Upload reference image selhal: {ex.Message}";
                    Log.Warning(ex, "GenerateAsync: upload reference selhalo");
                    return;
                }
            }

            // FLUX Schnell má default 4 steps. Img2img s denoise = 1 - strength
            // by ale kompletně přeskočilo většinu kroků (4 × 0.3 = 1.2 step → blob).
            // Když máme reference, dorovnáme steps tak, aby efektivních zbylo aspoň 6.
            int effectiveSteps = Steps;
            if (uploadedRefNames.Count > 0)
            {
                var denoise = Math.Clamp(1.0 - ReferenceStrength, 0.0, 1.0);
                if (denoise > 0)
                {
                    var minEffective = isFlux ? 6 : 12;
                    effectiveSteps = Math.Max(Steps, (int)Math.Ceiling(minEffective / denoise));
                }
            }

            // Workflow router:
            //   • FLUX GGUF  → UnetLoaderGGUF + DualCLIPLoader + VAELoader (4-loader workflow)
            //   • FLUX safetensors → klasický CheckpointLoaderSimple (all-in-one)
            //   • SDXL / SD  → klasický CheckpointLoaderSimple
            //
            // FLUX GGUF předpokládá, že uživatel má v Models složce CLIP-L, T5 a VAE
            // (najde je přes extra_model_paths.yaml). Pokud chybí, ComfyUI vrátí
            // validation error a my ho v UI ukážeme.
            Dictionary<string, object> workflow;
            string emptyLatentKey;
            string ksamplerKey;
            object vaeRef;

            if (isFlux && isGguf)
            {
                workflow = ComfyWorkflowBuilder.BuildFluxGguf(
                    SelectedModel,
                    ComfyWorkflowBuilder.DefaultFluxClipL,
                    ComfyWorkflowBuilder.DefaultFluxT5,
                    ComfyWorkflowBuilder.DefaultFluxVae,
                    Prompt, res.W, res.H, effectiveSteps, Cfg, seed, VariantCount);
                emptyLatentKey = ComfyWorkflowBuilder.FluxGgufEmptyLatentKey;
                ksamplerKey    = ComfyWorkflowBuilder.FluxGgufKSamplerKey;
                vaeRef         = ComfyWorkflowBuilder.FluxGgufVaeRef;
            }
            else if (isFlux)
            {
                workflow = ComfyWorkflowBuilder.BuildFlux(
                    SelectedModel, Prompt, res.W, res.H, effectiveSteps, Cfg, seed, VariantCount);
                emptyLatentKey = ComfyWorkflowBuilder.FluxEmptyLatentKey;
                ksamplerKey    = ComfyWorkflowBuilder.FluxKSamplerKey;
                vaeRef         = ComfyWorkflowBuilder.FluxVaeRef;
            }
            else
            {
                workflow = ComfyWorkflowBuilder.BuildStandard(
                    SelectedModel, Prompt, NegativePrompt, res.W, res.H, effectiveSteps, Cfg, seed, VariantCount);
                emptyLatentKey = ComfyWorkflowBuilder.StandardEmptyLatentKey;
                ksamplerKey    = ComfyWorkflowBuilder.StandardKSamplerKey;
                vaeRef         = ComfyWorkflowBuilder.StandardVaeRef;
            }

            // Pokud máme reference, přepneme workflow na img2img režim:
            // EmptyLatentImage se odstraní, místo něj jde do KSampleru blendnutý
            // latent z referenčních obrázků. Denoise = 1 - strength.
            if (uploadedRefNames.Count > 0)
            {
                ComfyWorkflowBuilder.InjectReferenceImages(
                    workflow,
                    emptyLatentKey,
                    ksamplerKey,
                    vaeRef,
                    uploadedRefNames,
                    res.W, res.H,
                    ReferenceStrength,
                    VariantCount);
            }

            GenerationStatus = "Odesílám do fronty…";
            var promptId = await _comfy.QueuePromptAsync(workflow, cts.Token);
            GenerationStatus = "Generuji…";

            var progress = new Progress<int>(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    GenerationProgress = p;
                    GenerationStatus   = p < 100 ? $"Generuji… {p} %" : "Ukládám…";
                });
            });

            var result = await _comfy.WaitForResultAsync(promptId, progress, cts.Token);
            if (result is null)
            {
                GenerationStatus = "Zrušeno";
                return;
            }

            var outputDir = GetOutputDirectory();
            Directory.CreateDirectory(outputDir);

            foreach (var imgRef in result.Images)
            {
                var bytes    = await _comfy.DownloadImageAsync(imgRef.Filename, imgRef.Subfolder, imgRef.Type, cts.Token);
                var fileName = $"AIStudio_{DateTime.Now:yyyyMMdd_HHmmss}_{imgRef.Filename}";
                var filePath = Path.Combine(outputDir, fileName);
                await File.WriteAllBytesAsync(filePath, bytes, cts.Token);

                var now      = DateTime.Now;
                var imageId  = Guid.NewGuid().ToString();   // jeden GUID pro VM i DB záznam, aby Delete fungoval
                var vm       = new GeneratedImageViewModel
                {
                    Id        = imageId,
                    FilePath  = filePath,
                    Prompt    = Prompt,
                    Model     = SelectedModel,
                    Seed      = seed,
                    Width     = res.W,
                    Height    = res.H,
                    Timestamp = now,
                };

                // Persist to SQLite — fire-and-forget with logging
                var record = new ImageRecord(
                    imageId,
                    filePath,
                    Prompt,
                    SelectedModel,
                    seed,
                    res.W,
                    res.H,
                    Steps,
                    Cfg,
                    now);

                _ = TrySaveImageAsync(record);

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    GeneratedImages.Insert(0, vm);
                    LatestImage = vm;
                    OnPropertyChanged(nameof(HasGeneratedImages));
                });
            }

            GenerationStatus = $"Hotovo! ({result.Images.Count} obrázek)";
        }
        catch (OperationCanceledException)
        {
            GenerationStatus = "Zrušeno";
        }
        catch (Exception ex)
        {
            GenerationStatus = $"Chyba: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
            _genCts      = null;
        }
    }

    [RelayCommand]
    private void StopGeneration() => _genCts?.Cancel();

    /// <summary>
    /// Routovací command pro hlavní Generate tlačítko v UI — podle <see cref="IsSmartMode"/>
    /// volá buď <see cref="GenerateSmartAsync"/> (intent parser → auto-fill → Generate),
    /// nebo přímo <see cref="GenerateAsync"/>. UI tak má jediné tlačítko a uživatel
    /// nemusí přepínat manuálně.
    /// </summary>
    [RelayCommand]
    private async Task GenerateRoutedAsync()
    {
        if (IsSmartMode) await GenerateSmartAsync();
        else             await GenerateAsync();
    }

    /// <summary>Přepíná Smart/Manual mód — volá segmented toggle v UI.</summary>
    [RelayCommand]
    private void SetSmartMode(string value)
        => IsSmartMode = string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Setter pro AspectRatio z UI ikon (Čtverec / Šířka / Výška) ve Smart módu.
    /// CommandParameter je název enum hodnoty jako string (např. "R1x1").
    /// </summary>
    [RelayCommand]
    private void SetAspect(string value)
    {
        if (Enum.TryParse<AspectRatio>(value, out var ar))
            SelectedAspectRatio = ar;
    }

    // ── Smart generování ──────────────────────────────────────────────────────

    /// <summary>
    /// Smart flow: surový popis → LLM intent parser → vybere model + prompt + aspect
    /// + quality + negative → naplní stávající ImageGenerator pole → spustí standardní
    /// <see cref="GenerateAsync"/>. Uživatel vidí transparentně co se vybralo
    /// (badge „Vybral jsem epicrealismXL, protože…") a může v Manual módu doladit.
    /// </summary>
    [RelayCommand]
    private async Task GenerateSmartAsync()
    {
        if (string.IsNullOrWhiteSpace(SmartPrompt))
        {
            GenerationStatus = "Zadej popis obrázku";
            return;
        }
        if (!_comfy.IsRunning)
        {
            GenerationStatus = "ComfyUI není spuštěno";
            return;
        }

        IsParsingIntent  = true;
        SmartReasoning   = string.Empty;

        // Auto-load chat LLM, pokud není načtený. Bez něho parser neprovede
        // překlad cz→en a expanze, takže prompt jde do SDXL syrový a kočka
        // v klobouku skončí jako náhodné fotorealistické zvíře (klasika).
        if (!_llama.IsLoaded)
        {
            GenerationStatus = "Načítám chat model pro Smart parser…";
            var loaded = await EnsureLlmLoadedAsync();
            if (!loaded)
            {
                GenerationStatus = "Pro Smart režim potřebuješ stažený chat model. " +
                                   "Přejdi do Modely → Doporučené, stáhni Llama 3.1 8B " +
                                   "nebo jiný malý chat GGUF, a zkus to znovu.";
                IsParsingIntent = false;
                return;
            }
        }

        GenerationStatus = "Analyzuji popis…";

        ImageIntent intent;
        try
        {
            intent = await _intentParser.ParseAsync(SmartPrompt);
        }
        catch (Exception ex)
        {
            // Parser by neměl házet (má vnitřní fallback), ale jistota nikoho nezabije
            Log.Warning(ex, "GenerateSmartAsync: parser hodil výjimku — použiju raw prompt");
            intent = new ImageIntent(
                ImageKind.Auto, ImageAspect.Square, ImageQualityHint.Normal,
                SmartPrompt, "blurry, low quality, watermark", $"Fallback: {ex.Message}");
        }
        finally
        {
            IsParsingIntent = false;
        }

        // Načteme aktuální dostupné modely (pro případ, že uživatel mezitím
        // něco stáhl) — checkpoints jsou populované přes LoadCheckpointsAsync,
        // takže je jen synchronně přečteme.
        var availableNames = AvailableCheckpoints.ToList();
        var pickedModel    = _modelMatcher.Match(intent.Kind, availableNames);

        if (string.IsNullOrEmpty(pickedModel))
        {
            GenerationStatus = "Žádný stažený model — přejdi do Modely a stáhni alespoň jeden checkpoint.";
            return;
        }

        // Auto-fill stávající pole — Manual UI se okamžitě aktualizuje (data binding),
        // takže když uživatel přepne přes Smart→Manual, vidí přesně co Smart navrhl.
        SelectedModel        = pickedModel;
        Prompt               = intent.EnglishPrompt;
        NegativePrompt       = intent.NegativePrompt;
        SelectedAspectRatio  = MapAspect(intent.Aspect);
        SelectedQuality      = MapQuality(intent.Quality);

        // Sestavíme transparency text. Zkrácený model name (bez přípony) pro lidskou hláškou.
        var modelDisplay = Path.GetFileNameWithoutExtension(pickedModel);
        SmartReasoning   = $"Model: {modelDisplay} · {intent.Kind} · {intent.Aspect}" +
                           (string.IsNullOrEmpty(intent.Reasoning) ? "" : $" — {intent.Reasoning}");

        Log.Information("GenerateSmart: kind={Kind} aspect={Aspect} model='{Model}' prompt='{Prompt}'",
            intent.Kind, intent.Aspect, modelDisplay, Trunc(intent.EnglishPrompt, 100));

        // Spustíme klasické generování
        await GenerateAsync();
    }

    private static AspectRatio MapAspect(ImageAspect a) => a switch
    {
        ImageAspect.Landscape => AspectRatio.R16x9,
        ImageAspect.Portrait  => AspectRatio.R9x16,
        _                     => AspectRatio.R1x1,
    };

    private static ImageQuality MapQuality(ImageQualityHint q) => q switch
    {
        ImageQualityHint.Fast    => ImageQuality.SD,
        ImageQualityHint.HighRes => ImageQuality.QHD,
        _                        => ImageQuality.FHD,
    };

    private static string Trunc(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" :
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Najde a načte chat .gguf model do LlamaService, pokud žádný není načtený.
    /// Priorita:
    ///   1) <see cref="AppSettings.DefaultChatModelName"/> (uživatel si vybral v Modely)
    ///   2) první chat .gguf v Models/ (vyloučí FLUX deps a image GGUFy podle heuristiky)
    /// Vrací true při úspěchu, false když není co načíst nebo load selže.
    /// </summary>
    private async Task<bool> EnsureLlmLoadedAsync()
    {
        if (_llama.IsLoaded) return true;

        var modelsDir = string.IsNullOrWhiteSpace(_settings.Settings.ModelsDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                           "AIStudio", "Models")
            : _settings.Settings.ModelsDirectory;

        if (!Directory.Exists(modelsDir))
        {
            Log.Warning("EnsureLlm: Models adresář {Dir} neexistuje", modelsDir);
            return false;
        }

        // Vybereme kandidátní GGUF — image FLUX gguf začínají "flux", což je
        // pro chat nepoužitelné. Heuristika: nezačíná-li name na "flux", je to
        // chat GGUF.
        var candidates = Directory
            .EnumerateFiles(modelsDir, "*.gguf", SearchOption.AllDirectories)
            .Where(p => !Path.GetFileName(p).StartsWith("flux", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            Log.Warning("EnsureLlm: žádný chat .gguf v {Dir}", modelsDir);
            return false;
        }

        // Preferuj uživatelův default chat model, pokud je v kandidátech
        var defaultName = _settings.Settings.DefaultChatModelName;
        var preferred = !string.IsNullOrEmpty(defaultName)
            ? candidates.FirstOrDefault(p =>
                Path.GetFileNameWithoutExtension(p).Contains(defaultName, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(p).Contains(defaultName.Replace(" ", "-"), StringComparison.OrdinalIgnoreCase))
            : null;

        // Jinak preferuj nejmenší soubor — rychlejší load + dost na intent parsing
        var path = preferred ?? candidates.OrderBy(p => new FileInfo(p).Length).First();
        var name = Path.GetFileNameWithoutExtension(path);

        try
        {
            Log.Information("EnsureLlm: načítám {Path} (size={Size:F1} GB)",
                path, new FileInfo(path).Length / 1_073_741_824.0);
            await _llama.LoadModelAsync(path, name);
            return _llama.IsLoaded;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "EnsureLlm: load selhalo pro {Path}", path);
            return false;
        }
    }

    /// <summary>Klik na thumbnail v pásu — zobrazí obrázek v hlavním canvasu.</summary>
    [RelayCommand]
    private void SelectImage(GeneratedImageViewModel img)
    {
        // Deselect previous
        if (LatestImage is { } prev && !ReferenceEquals(prev, img))
            prev.IsSelected = false;
        img.IsSelected = true;
        LatestImage = img;
    }

    /// <summary>
    /// Smaže obrázek ze tří míst: galerie (UI), DB (metadata) a disku (.png).
    /// Selhání disk-delete nebrání odstranění z galerie — soubor mohl být ručně přesunut.
    /// </summary>
    [RelayCommand]
    private async Task DeleteImageAsync(GeneratedImageViewModel img)
    {
        if (img is null) return;

        // 1) UI — odstranit z galerie a vybrat jiný obrázek
        var idx = GeneratedImages.IndexOf(img);
        GeneratedImages.Remove(img);
        OnPropertyChanged(nameof(HasGeneratedImages));

        // Vyber sousedící obrázek (preferuje další, fallback předchozí, případně null)
        if (ReferenceEquals(LatestImage, img))
        {
            LatestImage = GeneratedImages.ElementAtOrDefault(idx)
                       ?? GeneratedImages.LastOrDefault();
            if (LatestImage is not null) LatestImage.IsSelected = true;
        }

        // 2) DB
        try { await _imageRepo.DeleteImageAsync(img.Id); }
        catch (Exception ex) { Log.Warning(ex, "DeleteImage: DB delete selhal pro {Id}", img.Id); }

        // 3) Disk (best effort)
        try
        {
            if (File.Exists(img.FilePath)) File.Delete(img.FilePath);
            Log.Information("DeleteImage: smazán {Path}", img.FilePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DeleteImage: nelze smazat soubor {Path}", img.FilePath);
        }
    }

    [RelayCommand]
    private void ClearReferenceImage()
    {
        ReferenceImagePath = null;
        HasReferenceImage  = false;
        ReferenceImages.Clear();
        OnPropertyChanged(nameof(HasReferenceImages));
    }

    /// <summary>Odstraní jeden konkrétní reference image z collection.</summary>
    [RelayCommand]
    private void RemoveReferenceImage(ReferenceImageItem item)
    {
        if (item is null) return;
        ReferenceImages.Remove(item);

        // Synchronizuj single-image kompatibilitu
        ReferenceImagePath = ReferenceImages.FirstOrDefault()?.Path;
        HasReferenceImage  = ReferenceImages.Count > 0;
        OnPropertyChanged(nameof(HasReferenceImages));
    }

    [RelayCommand]
    private async Task PickReferenceImageAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;

        // Multi-select — uživatel může přidat víc obrázků naráz (Ctrl+klik).
        var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Vybrat referenční obrázky",
            AllowMultiple  = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Obrázky") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" } }
            }
        });

        if (files is null || files.Count == 0) return;

        foreach (var f in files)
        {
            var path = f.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) continue;

            // Skip duplicates
            if (ReferenceImages.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            ReferenceImages.Add(new ReferenceImageItem(path));
        }

        // Aktualizuj legacy single-image properties (první obrázek = primární)
        ReferenceImagePath = ReferenceImages.FirstOrDefault()?.Path;
        HasReferenceImage  = ReferenceImages.Count > 0;
        OnPropertyChanged(nameof(HasReferenceImages));
    }

    [RelayCommand]
    private void RandomizeSeed() => Seed = Random.Shared.Next(1, int.MaxValue);

    // ── DB helpers ────────────────────────────────────────────────────────────

    private async Task TrySaveImageAsync(ImageRecord record)
    {
        try
        {
            await _imageRepo.SaveImageAsync(record);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to persist image record {FilePath}", record.FilePath);
        }
    }

    /// <summary>Naplní GeneratedImages záznamy z DB (volá se při startu).</summary>
    public async Task LoadSavedImagesAsync()
    {
        try
        {
            var records = await _imageRepo.LoadAllImagesAsync();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                GeneratedImages.Clear();
                foreach (var rec in records)
                {
                    // Přeskoč záznamy, jejichž soubor byl ručně smazán
                    if (!File.Exists(rec.FilePath)) continue;

                    var vm = new GeneratedImageViewModel
                    {
                        Id        = rec.Id,        // ← stejné ID jako v DB, aby Delete našel správný řádek
                        FilePath  = rec.FilePath,
                        Prompt    = rec.Prompt,
                        Model     = rec.ModelName,
                        Seed      = rec.Seed,
                        Width     = rec.Width,
                        Height    = rec.Height,
                        Timestamp = rec.GeneratedAt,
                    };
                    GeneratedImages.Add(vm);
                }

                LatestImage = GeneratedImages.Count > 0 ? GeneratedImages[0] : null;
                OnPropertyChanged(nameof(HasGeneratedImages));
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load saved images from DB");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string GetOutputDirectory()
    {
        var modelsDir = _settings.Settings.ModelsDirectory;
        if (!string.IsNullOrWhiteSpace(modelsDir))
        {
            var parent = Path.GetDirectoryName(modelsDir);
            if (parent is not null)
                return Path.Combine(parent, "AIStudio_Output");
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "AIStudio");
    }

    /// <summary>
    /// Zkontroluje, jestli v Models složce existují soubory potřebné pro FLUX GGUF
    /// workflow (CLIP-L, T5, VAE). Vrátí seznam přátelských názvů těch chybějících.
    /// Hledá pohledem do Models adresáře — extra_model_paths.yaml mapuje subdirs
    /// (clip/, vae/) i samotný root, takže to opravdu pokrývá obě varianty.
    /// </summary>
    private List<string> FindMissingFluxDependencies()
    {
        var modelsDir = string.IsNullOrWhiteSpace(_settings.Settings.ModelsDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                           "AIStudio", "Models")
            : _settings.Settings.ModelsDirectory;

        var missing = new List<string>();

        var deps = new (string Label, string FileName, string Subdir)[]
        {
            ("CLIP-L", ComfyWorkflowBuilder.DefaultFluxClipL, "clip"),
            ("T5",     ComfyWorkflowBuilder.DefaultFluxT5,    "clip"),
            ("VAE",    ComfyWorkflowBuilder.DefaultFluxVae,   "vae"),
        };

        foreach (var (label, file, sub) in deps)
        {
            // Zkontrolujeme primární umístění (root) i podsložku — přesně tak,
            // jak ComfyUI hledá přes extra_model_paths.yaml.
            var inRoot = Path.Combine(modelsDir, file);
            var inSub  = Path.Combine(modelsDir, sub, file);
            if (!File.Exists(inRoot) && !File.Exists(inSub))
                missing.Add(label);
        }

        return missing;
    }
}
