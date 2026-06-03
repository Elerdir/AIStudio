using System.Collections.ObjectModel;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;
using AIStudio.Infrastructure.Services;

namespace AIStudio.App.ViewModels.ImageStudio;

public enum AspectRatio { R1x1, R16x9, R9x16, R4x3, R3x4, R21x9 }
public enum ImageQuality { SD, FHD, QHD, UHD4K }

public partial class ImageGeneratorViewModel : ViewModelBase
{
    private readonly IComfyService           _comfy;
    private readonly ISettingsService        _settings;
    private readonly IImageRepository        _imageRepo;
    private readonly ILoraLibraryService     _loraLibrary;
    private readonly IImageIntentParser?     _intentParser;
    private readonly IImageModelMatcher?     _modelMatcher;
    private readonly ILlamaService?          _llama;
    private readonly IFluxDependencyService? _fluxDeps;
    private readonly IUpscaleModelService?   _upscaleService;
    private static int _counter;

    private CancellationTokenSource? _genCts;

    [ObservableProperty] private string _title;
    [ObservableProperty] private bool   _isSmartMode = true;
    [ObservableProperty] private string _czechDescription = string.Empty;
    [ObservableProperty] private string _prompt = string.Empty;
    [ObservableProperty] private string _negativePrompt = string.Empty;
    [ObservableProperty] private string _selectedModel = "FLUX.1 Schnell";
    [ObservableProperty] private int _steps = 4;
    [ObservableProperty] private double _cfg = 1.0;
    [ObservableProperty] private long _seed = -1;
    [ObservableProperty] private int _variantCount = 1;
    [ObservableProperty] private AspectRatio _selectedAspectRatio = AspectRatio.R1x1;
    [ObservableProperty] private ImageQuality _selectedQuality = ImageQuality.FHD;
    [ObservableProperty] private string _selectedSampler   = ComfyWorkflowBuilder.DefaultSamplerSd;
    [ObservableProperty] private string _selectedScheduler = ComfyWorkflowBuilder.DefaultSchedulerSd;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private bool _isZoomed;
    [ObservableProperty] private int _generationProgress;
    [ObservableProperty] private string _generationStatus = string.Empty;

    // ── Referenční obrázky ────────────────────────────────────────────────────
    // Uživatel může přidat více obrázků jako inspiraci.
    // Img2img workflow použije vždy první obrázek ze seznamu jako výchozí bod;
    // ostatní budou postupně zahrnuty, jakmile přidáme IP-Adapter podporu.

    /// <summary>
    /// 0 = výsledek blízký referenci, 1 = výsledek blízký promptu (jen inspirace referencí).
    /// Mapuje na denoise v img2img: CreativityToDenoise(0.6) ≈ 0.78 → prompt vede 78 %.
    /// </summary>
    [ObservableProperty] private double _referenceStrength = 0.6;
    [ObservableProperty] private GeneratedImageViewModel? _latestImage;

    /// <summary>
    /// Upscale po vygenerování — hires fix (2. průchod modelu, víc detailu/hloubky)
    /// + ESRGAN model upscaler (rozlišení + ostrost), výsledek 2× base. Opt-in:
    /// je pomalejší a při prvním použití stáhne ~64 MB ESRGAN model.
    /// </summary>
    [ObservableProperty] private bool _enableUpscale;

    public ObservableCollection<string> ReferenceImagePaths { get; } = new();

    public bool HasReferenceImage => ReferenceImagePaths.Count > 0;

    // Available checkpoints fetched from ComfyUI
    [ObservableProperty] private ObservableCollection<string> _availableCheckpoints = new();

    // ── LoRA podpora ──────────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<string> _availableLoras = new();
    [ObservableProperty] private string? _selectedLoraToAdd;
    [ObservableProperty] private double  _loraStrength = 1.0;

    /// <summary>Aktivní LoRA adaptery s nastavenou silou. Pořadí = pořadí načítání.</summary>
    public ObservableCollection<LoraItem> SelectedLoras { get; } = new();

    public bool HasLoras => SelectedLoras.Count > 0;

    // ── Galerie — stránkované načítání ───────────────────────────────────────
    // Při 5k+ obrázcích by načtení všech do ObservableCollection zamrzlo UI
    // (memory + bitmapa preview). Stránkujeme po 50, „Načíst další" tlačítko
    // dotahuje další várku ze SQLite.

    /// <summary>Velikost stránky pro galerii. Konzervativní default — odladěno pro 8 GB RAM.</summary>
    private const int GalleryPageSize = 50;

    /// <summary>Image Studio ukazuje jen obrázky — videa patří do Video tabu / Galerie
    /// (mp4 navíc nejde dekódovat jako náhled → černé čtverce).</summary>
    private static readonly AIStudio.Core.Models.ImageQueryFilter ImagesOnlyFilter =
        new(MediaType: AIStudio.Core.Models.MediaTypes.Image);

    public ObservableCollection<GeneratedImageViewModel> GeneratedImages { get; } = new();

    /// <summary>Celkový počet záznamů v DB (může být větší než GeneratedImages.Count).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GalleryStatusLine), nameof(CanLoadMoreImages))]
    private int _totalImagesInDb;

    /// <summary>True když ještě nejsou stažené stránky se vším co je v DB.</summary>
    public bool CanLoadMoreImages => GeneratedImages.Count < TotalImagesInDb;

    /// <summary>Lidsky čitelný status — „Zobrazeno 50 z 1247".</summary>
    public string GalleryStatusLine => TotalImagesInDb switch
    {
        0    => string.Empty,
        var t when GeneratedImages.Count >= t => $"Zobrazeno všech {t}",
        var t => $"Zobrazeno {GeneratedImages.Count} z {t}"
    };

    /// <summary>Probíhá načítání další stránky? Brání dvojkliku.</summary>
    [ObservableProperty] private bool _isLoadingMoreImages;

    public static IReadOnlyList<AspectRatio> AspectRatios { get; } = Enum.GetValues<AspectRatio>();
    public static IReadOnlyList<ImageQuality> Qualities { get; }   = Enum.GetValues<ImageQuality>();

    public static IReadOnlyList<string> Samplers { get; } = new[]
    {
        "euler", "euler_ancestral", "dpmpp_2m", "dpmpp_sde", "dpmpp_2m_sde", "lcm", "ddim", "heun"
    };

    public static IReadOnlyList<string> Schedulers { get; } = new[]
    {
        "normal", "karras", "simple", "exponential", "sgm_uniform", "beta"
    };

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

    public bool HasGeneratedImages => GeneratedImages.Count > 0;

    public bool IsManualMode      => !IsSmartMode;

    /// <summary>
    /// True pokud je Smart mód aktivní, ale LLM není načten.
    /// V tom případě se český popis použije přímo jako prompt (bez překladu).
    /// Slouží pro zobrazení info banneru v UI.
    /// </summary>
    public bool IsSmartModeWithoutLlm => IsSmartMode && (_llama is null || !_llama.IsLoaded);

    // Orientace — zjednodušené 3-tlačítko pro Smart mód
    public bool OrientationSquare    => SelectedAspectRatio == AspectRatio.R1x1;
    public bool OrientationPortrait  => SelectedAspectRatio is AspectRatio.R9x16 or AspectRatio.R3x4;
    public bool OrientationLandscape => SelectedAspectRatio is AspectRatio.R16x9 or AspectRatio.R4x3;

    public ImageGeneratorViewModel(
        IComfyService            comfy,
        ISettingsService         settings,
        IImageRepository         imageRepo,
        ILoraLibraryService      loraLibrary,
        IImageIntentParser?      intentParser = null,
        IImageModelMatcher?      modelMatcher = null,
        ILlamaService?           llama        = null,
        IFluxDependencyService?  fluxDeps     = null,
        IUpscaleModelService?    upscaleService = null)
    {
        _comfy        = comfy;
        _settings     = settings;
        _imageRepo    = imageRepo;
        _loraLibrary  = loraLibrary;
        _intentParser = intentParser;
        _modelMatcher = modelMatcher;
        _llama        = llama;
        _fluxDeps     = fluxDeps;
        _upscaleService = upscaleService;
        var n = System.Threading.Interlocked.Increment(ref _counter);
        _title = $"Generátor {n}";

        ReferenceImagePaths.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(HasReferenceImage));
        SelectedLoras.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(HasLoras));

        UpdateModelDefaults(SelectedModel);

        // Ihned naskenuj lokální LoRA soubory — uživatel vidí dropdown naplněný
        // bez nutnosti klikat „Načíst". Refresh přes tlačítko načte i ComfyUI seznam.
        _ = LoadLorasAsync();
    }

    // ── Property hooks ────────────────────────────────────────────────────────

    partial void OnIsSmartModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsManualMode));
        OnPropertyChanged(nameof(IsSmartModeWithoutLlm));
    }

    partial void OnSelectedAspectRatioChanged(AspectRatio value)
    {
        OnPropertyChanged(nameof(AspectRatioLabel));
        OnPropertyChanged(nameof(ResolutionLabel));
        OnPropertyChanged(nameof(OrientationSquare));
        OnPropertyChanged(nameof(OrientationPortrait));
        OnPropertyChanged(nameof(OrientationLandscape));
    }

    partial void OnSelectedQualityChanged(ImageQuality value)
    {
        OnPropertyChanged(nameof(QualityLabel));
        OnPropertyChanged(nameof(ResolutionLabel));
    }

    [RelayCommand] void SetSmartMode()  => IsSmartMode = true;
    [RelayCommand] void SetManualMode() => IsSmartMode = false;

    [RelayCommand] void ToggleZoom() => IsZoomed = !IsZoomed;
    [RelayCommand] void CloseZoom()  => IsZoomed = false;

    [RelayCommand]
    private void UseAsReference(GeneratedImageViewModel img)
    {
        if (!File.Exists(img.FilePath)) return;
        if (!ReferenceImagePaths.Contains(img.FilePath))
            ReferenceImagePaths.Add(img.FilePath);
    }

    [RelayCommand] void SetOrientationSquare()   => SelectedAspectRatio = AspectRatio.R1x1;
    [RelayCommand] void SetOrientationPortrait()  => SelectedAspectRatio = AspectRatio.R9x16;
    [RelayCommand] void SetOrientationLandscape() => SelectedAspectRatio = AspectRatio.R16x9;

    partial void OnSelectedModelChanged(string value) => UpdateModelDefaults(value);

    private void UpdateModelDefaults(string model)
    {
        if (ComfyWorkflowBuilder.IsFluxModel(model))
        {
            var (steps, guidance) = ComfyWorkflowBuilder.FluxDefaults(model);
            Steps             = steps;
            Cfg               = guidance;
            SelectedSampler   = ComfyWorkflowBuilder.DefaultSamplerFlux;
            SelectedScheduler = ComfyWorkflowBuilder.DefaultSchedulerFlux;
        }
        else
        {
            Steps             = 20;
            Cfg               = 7.0;
            SelectedSampler   = ComfyWorkflowBuilder.DefaultSamplerSd;
            SelectedScheduler = ComfyWorkflowBuilder.DefaultSchedulerSd;
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
                foreach (var name in fromComfy)
                {
                    // ComfyUI vrací i ControlNet/VAE/LoRA soubory, protože extra_model_paths.yaml
                    // mapuje root "." na všechny kategorie. Filtrujeme stejně jako lokální sken.
                    var fn = Path.GetFileName(name.Replace('/', Path.DirectorySeparatorChar));
                    if (FluxDependencyService.IsFluxDep(fn)) continue;
                    if (IsComfyPathExcluded(name))           continue;
                    combined.Add(name);
                }
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
    // Podadresáře ComfyUI, které NEJSOU checkpointy — jejich soubory se
    // nesmí zobrazovat v pickeru modelů pro generování.
    private static readonly HashSet<string> ExcludedModelSubdirs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "controlnet", "control_net", "control-net",
            "vae", "vae-approx", "vae_approx",
            "lora", "loras",
            "clip", "clip_vision", "clip-vision",
            "embeddings", "embedding",
            "upscale_models", "upscale", "esrgan",
            "hypernetworks", "hypernetwork",
            "photomaker",
            "ipadapter", "ip_adapter", "ip-adapter",
            "style_models",
            "gligen",
            "animatediff_models",
        };

    /// <summary>
    /// Vrátí true pokud soubor leží v podadresáři, který ComfyUI vyhrazuje
    /// pro jiný typ modelu než checkpoint (ControlNet, VAE, LoRA, …).
    /// Používá se pro lokální skenování (absolutní cesta + base dir).
    /// </summary>
    /// <summary>
    /// True, pokud název modelu obsahuje segment složky <c>unet/</c> nebo
    /// <c>diffusion_models/</c> (např. „unet\flux1-dev-fp8.safetensors"). Takové
    /// FLUX soubory jsou diffusion-model-only (bez CLIP/VAE) a musí se zavádět
    /// přes UNETLoader, ne CheckpointLoaderSimple.
    /// </summary>
    private static bool IsInDiffusionModelDir(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        var parts = modelName.Split('/', '\\');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("unet", StringComparison.OrdinalIgnoreCase) ||
                parts[i].Equals("diffusion_models", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsInExcludedSubdir(string filePath, string baseDir)
    {
        var relative = Path.GetRelativePath(baseDir, filePath);
        // Rozlož na části a projdi všechny adresáře (vše kromě posledního prvku = souboru)
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (ExcludedModelSubdirs.Contains(parts[i]))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Vrátí true pokud ComfyUI relativní cesta (oddělená '/' nebo '\') obsahuje
    /// podadresář vyhrazený pro jiný typ modelu (ControlNet, VAE, LoRA…),
    /// nebo pokud je jméno souboru charakteristické pro non-checkpoint typ.
    /// Používá se pro filtrování výstupu <see cref="IComfyService.GetCheckpointsAsync"/>,
    /// protože extra_model_paths.yaml mapuje root '.', takže ComfyUI vidí vše.
    /// </summary>
    private static bool IsComfyPathExcluded(string comfyRelativePath)
    {
        // Cesta může být "model.safetensors" nebo "subdir/model.safetensors"
        var parts = comfyRelativePath.Split('/', '\\', Path.DirectorySeparatorChar);
        // Projdi všechny komponenty KROMĚ posledního (= souboru) — detekce dle složky
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (ExcludedModelSubdirs.Contains(parts[i]))
                return true;
        }
        // Detekce dle názvu souboru — ControlNets/VAE/LoRA v rootu nemají subdirektář,
        // ale název je obvykle prozradí ("control_*", "vae-*", "lora_*" apod.).
        var fileName = parts[^1].ToLowerInvariant();
        if (fileName.StartsWith("control_")     || fileName.StartsWith("controlnet"))   return true;
        if (fileName.StartsWith("vae-")         || fileName.StartsWith("vae_"))         return true;
        if (fileName.StartsWith("lora_")        || fileName.StartsWith("lora-"))        return true;
        if (fileName.StartsWith("hypernetwork_") || fileName.StartsWith("embedding_")) return true;
        return false;
    }

    /// <summary>
    /// Najde v Models/ složce všechny soubory, které vypadají jako image checkpointy.
    /// .safetensors je přímo SDXL/SD/FLUX; .gguf jen pokud je to FLUX nebo SD.
    /// Vynechává soubory z podsložek vyhrazených pro jiné typy (controlnet, vae, lora…).
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
            {
                var fn = Path.GetFileName(path);

                // Přeskoč FLUX pomocné soubory (clip_l, t5xxl, ae)
                if (FluxDependencyService.IsFluxDep(fn))
                    continue;

                // Přeskoč soubory v podadresářích pro jiné typy (controlnet, vae, lora…)
                if (IsInExcludedSubdir(path, dir))
                    continue;

                found.Add(fn);
            }

            // Image GGUF — FLUX nebo SD difúzní modely
            foreach (var path in Directory.EnumerateFiles(dir, "*.gguf", SearchOption.AllDirectories))
            {
                if (IsInExcludedSubdir(path, dir)) continue;

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

        // Smart mód: přeložíme český popis → Prompt (přes IntentParser nebo přímo)
        if (IsSmartMode)
        {
            if (string.IsNullOrWhiteSpace(CzechDescription))
            {
                GenerationStatus = "Zadej popis obrázku";
                return;
            }

            if (_intentParser is not null)
            {
                GenerationStatus = "Analyzuji popis…";
                try
                {
                    var intent    = await _intentParser.ParseAsync(CzechDescription);
                    var usedLlm   = _llama is { IsLoaded: true }
                                    && !intent.Reasoning.StartsWith("Fallback", StringComparison.OrdinalIgnoreCase)
                                    && !intent.Reasoning.StartsWith("LLM není", StringComparison.OrdinalIgnoreCase);
                    Prompt         = intent.EnglishPrompt;
                    NegativePrompt = intent.NegativePrompt ?? string.Empty;
                    if (!usedLlm)
                        Log.Information("Smart mód: LLM fallback — popis použit přímo jako prompt");
                    if (_modelMatcher is not null && string.IsNullOrEmpty(SelectedModel))
                    {
                        var pick = _modelMatcher.Match(intent.Kind, AvailableCheckpoints);
                        if (!string.IsNullOrEmpty(pick)) SelectedModel = pick;
                    }
                    // Orientaci z intentu aplikujeme jen pokud uživatel nezměnil ručně
                    if (usedLlm)
                    {
                        SelectedAspectRatio = intent.Aspect switch
                        {
                            Core.Models.ImageAspect.Landscape => AspectRatio.R16x9,
                            Core.Models.ImageAspect.Portrait  => AspectRatio.R9x16,
                            _                                  => AspectRatio.R1x1,
                        };
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Smart mód: IntentParser selhal, používám popis přímo");
                    Prompt = CzechDescription;
                }
            }
            else
            {
                Prompt = CzechDescription;
            }
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

        var generateStarted = DateTime.Now;

        try
        {
            var res    = Resolution;
            var seed   = Seed < 0 ? (long)Random.Shared.Next(1, int.MaxValue) : Seed;
            var isFlux = ComfyWorkflowBuilder.IsFluxModel(SelectedModel);
            var isGguf = ComfyWorkflowBuilder.IsGgufModel(SelectedModel);
            // FLUX .safetensors, který obsahuje JEN UNET (bez CLIP/VAE) — např.
            // flux1-dev-fp8 od Kijai. Zjistí se v pre-flight check B; pak se generuje
            // přes UNETLoader + samostatné FLUX deps (jako GGUF), ne CheckpointLoaderSimple.
            var fluxUnetOnly = false;

            Log.Information(
                "Generování zahájeno — mód={Mode}, model={Model}, seed={Seed}, " +
                "rozlišení={W}x{H}, kroky={Steps}, CFG={Cfg}, varianty={Count}, reference={RefCount}",
                IsSmartMode ? "Smart" : "Manuál",
                SelectedModel, seed, res.W, res.H, Steps, Cfg, VariantCount,
                ReferenceImagePaths.Count);

            // Pre-flight check A: FLUX GGUF potřebuje samostatné CLIP-L + T5 + VAE
            // soubory v Models složce. Když chybí, ušetříme kruhovou cestu přes
            // ComfyUI validation error a uživateli rovnou řekneme, co stáhnout.
            if (isFlux && isGguf)
            {
                var missing = _fluxDeps?.FindMissing(ResolveModelsDir()) ?? new List<string>();
                if (missing.Count > 0)
                {
                    // Pokud deps stahujeme na pozadí, ukažeme uživateli přesný stav
                    if (_fluxDeps is { IsDownloading: true })
                    {
                        GenerationStatus = $"⏳ {_fluxDeps.DownloadStatusLine} — počkej na dokončení a zkus znovu.";
                    }
                    else
                    {
                        GenerationStatus =
                            $"Chybí FLUX závislosti: {string.Join(", ", missing)}. " +
                            "Stáhni je v sekci Modely — položky s označením povinné.";
                    }
                    return;
                }
            }

            // Pre-flight check B: .safetensors model — čteme záhlaví a zjistíme,
            // jestli soubor je kompletní checkpoint nebo jen UNet bez CLIP/VAE.
            // Tím se vyhneme matoucí ComfyUI chybě "clip input is invalid: None".
            if (!isGguf && SelectedModel.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            {
                GenerationStatus = "Kontroluji formát modelu…";
                var modelPath = await Task.Run(() =>
                    SafetensorsInspector.FindModelPath(
                        SelectedModel,
                        _settings.Settings.ModelsDirectory,
                        _settings.Settings.ComfyUiDirectory));

                // FLUX soubor ležící ve složce unet/ nebo diffusion_models/ je vždy
                // diffusion-model-only (bez CLIP/VAE) — pozná se z prefixu cesty v názvu
                // ("unet\flux1-dev-fp8.safetensors"). Spolehlivější než čtení hlavičky,
                // které selže, když se soubor nepodaří najít na disku.
                var inUnetDir = IsInDiffusionModelDir(SelectedModel);

                SafetensorsComponents? components = null;
                if (modelPath is not null)
                    components = await Task.Run(() => SafetensorsInspector.Inspect(modelPath));

                var isUnetOnly = (components?.IsUnetOnly ?? false) || inUnetDir;
                if (isUnetOnly)
                {
                    {
                        if (isFlux)
                        {
                            // FLUX UNET-only (flux1-dev-fp8 apod.) umíme vygenerovat přes
                            // UNETLoader + samostatné CLIP-L/T5/VAE — pokud ty soubory máme.
                            var missingDeps = _fluxDeps?.FindMissing(ResolveModelsDir()) ?? new List<string>();
                            if (missingDeps.Count == 0)
                            {
                                fluxUnetOnly = true;   // generujeme přes BuildFluxUnet
                                Log.Information(
                                    "FLUX UNET-only detekován ({Model}) — workflow přes UNETLoader + DualCLIPLoader + VAELoader",
                                    SelectedModel);
                            }
                            else if (_fluxDeps is { IsDownloading: true })
                            {
                                GenerationStatus = $"⏳ {_fluxDeps.DownloadStatusLine} — počkej na dokončení a zkus znovu.";
                                return;
                            }
                            else
                            {
                                GenerationStatus =
                                    "⚠ FLUX UNet potřebuje samostatné CLIP/VAE soubory: " +
                                    $"{string.Join(", ", missingDeps)}. Stáhni je v sekci Modely (FLUX závislosti).";
                                return;
                            }
                        }
                        else
                        {
                            GenerationStatus = "⚠ Model obsahuje jen UNet (bez CLIP a VAE) — ověř, že máš kompletní checkpoint soubor.";
                            return;
                        }
                    }
                }

                GenerationStatus = "Odesílám do fronty…";
            }

            // ── Referenční obrázky → img2img ─────────────────────────────────
            // Prvním krokem je upload do ComfyUI input složky.
            // Pokud uživatel přidal více obrázků, použijeme jako primární referenci
            // první ze seznamu (ostatní prozatím přeskočíme, IP-Adapter přijde později).
            string? uploadedRefName = null;
            if (ReferenceImagePaths.Count > 0)
            {
                var primaryRef = ReferenceImagePaths[0];
                GenerationStatus = $"Nahrávám referenci ({Path.GetFileName(primaryRef)})…";
                try
                {
                    uploadedRefName = await _comfy.UploadImageAsync(primaryRef, cts.Token);
                    if (ReferenceImagePaths.Count > 1)
                        GenerationStatus = $"Reference nahrána ({ReferenceImagePaths.Count} obr., použita 1. jako základ)…";
                }
                catch (Exception ex)
                {
                    GenerationStatus = $"Upload reference selhal: {ex.Message}";
                    return;
                }
            }

            // GGUF + reference: img2img pro GGUF zatím neumíme → informuj a generuj
            // txt2img (composer referenci pro GGUF ignoruje). Hláška + pauza zůstává
            // ve VM (UI), routing dělá composer.
            if (uploadedRefName is not null && isGguf)
            {
                GenerationStatus = "ℹ️ Img2img není pro GGUF k dispozici — generuji bez reference.";
                await Task.Delay(1500, cts.Token);
            }

            // Upscale: zajisti ESRGAN model (async I/O zůstává ve VM). Při selhání
            // degradujeme na hires-fix-only (ten žádný extra model nepotřebuje).
            var useEsrgan = false;
            if (EnableUpscale)
            {
                useEsrgan = _upscaleService is not null;
                if (useEsrgan && !_upscaleService!.IsAvailable(ResolveModelsDir()))
                {
                    GenerationStatus = "Stahuji upscale model (~64 MB)…";
                    try { await _upscaleService.EnsureAsync(ResolveModelsDir(), null, cts.Token); }
                    catch (Exception ex) { Log.Warning(ex, "Upscale: stažení ESRGAN modelu selhalo"); }
                    useEsrgan = _upscaleService.IsAvailable(ResolveModelsDir());
                }
            }

            // Sestavení workflow (router + LoRA injection + upscale tail) — čistá
            // logika vytažená do ImageWorkflowComposer (testovaná samostatně).
            var workflow = ImageWorkflowComposer.Compose(new ImageWorkflowRequest(
                Model:             SelectedModel,
                Prompt:            Prompt,
                NegativePrompt:    NegativePrompt,
                Width:             res.W,
                Height:            res.H,
                Steps:             Steps,
                Cfg:               Cfg,
                Seed:              seed,
                BatchSize:         VariantCount,
                Sampler:           SelectedSampler,
                Scheduler:         SelectedScheduler,
                IsFlux:            isFlux,
                IsGguf:            isGguf,
                FluxUnetOnly:      fluxUnetOnly,
                UploadedRefName:   uploadedRefName,
                ReferenceStrength: ReferenceStrength,
                Loras:             SelectedLoras.ToList(),
                EnableUpscale:     EnableUpscale,
                UseEsrganModel:    useEsrgan,
                UpscaleModelName:  _upscaleService?.ModelFileName ?? ComfyWorkflowBuilder.DefaultUpscaleModel));

            if (EnableUpscale)
                Log.Information("Upscale zapnut — hires fix + ESRGAN={Esrgan}, výstup ~2× ({W}x{H} → {W2}x{H2})",
                    useEsrgan, res.W, res.H, res.W * 2, res.H * 2);

            Log.Information("Odesílám workflow do ComfyUI (model={Model}, sampler={Sampler}/{Scheduler})",
                            SelectedModel, SelectedSampler, SelectedScheduler);
            var promptId = await _comfy.QueuePromptAsync(workflow, cts.Token);
            Log.Information("ComfyUI přijal prompt (ID={PromptId})", promptId);
            GenerationStatus = "Generuji…";

            var progress = new Progress<int>(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    GenerationProgress = p;
                    GenerationStatus   = p >= 100 ? "Ukládám…"
                                       : p > 0   ? $"Generuji… {p} %"
                                                 : "Generuji…";
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
                // Vepiš AI-provenience do PNG metadat (AI-generated, model, aplikace).
                bytes        = AIStudio.Core.Services.PngTextMetadata.AddAiProvenance(bytes, SelectedModel, Prompt);
                var fileName = $"AIStudio_{DateTime.Now:yyyyMMdd_HHmmss}_{imgRef.Filename}";
                var filePath = Path.Combine(outputDir, fileName);
                await File.WriteAllBytesAsync(filePath, bytes, cts.Token);

                var now = DateTime.Now;
                var vm  = new GeneratedImageViewModel
                {
                    FilePath  = filePath,
                    Prompt    = Prompt,
                    Model     = SelectedModel,
                    Seed      = seed,
                    Width     = res.W,
                    Height    = res.H,
                    Timestamp = now,
                    Sampler   = SelectedSampler,
                    Scheduler = SelectedScheduler,
                    Steps     = Steps,
                    Cfg       = Cfg,
                };

                // Persist to SQLite — fire-and-forget with logging
                var record = new ImageRecord(
                    Guid.NewGuid().ToString(),
                    filePath,
                    Prompt,
                    SelectedModel,
                    seed,
                    res.W,
                    res.H,
                    Steps,
                    Cfg,
                    SelectedSampler,
                    SelectedScheduler,
                    now);

                _ = TrySaveImageAsync(record);

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    GeneratedImages.Insert(0, vm);
                    LatestImage = vm;
                    // Inkrementuj total count — udržuje status label „Zobrazeno X z Y"
                    TotalImagesInDb++;
                    OnPropertyChanged(nameof(HasGeneratedImages));
                });
            }

            var elapsed = DateTime.Now - generateStarted;
            Log.Information(
                "Generování úspěšně dokončeno — {Count} {Word}, trvalo {Elapsed}",
                result.Images.Count, PluralizeImages(result.Images.Count),
                elapsed.ToString(@"mm\:ss\.ff"));
            GenerationStatus = $"Hotovo! ({result.Images.Count} {PluralizeImages(result.Images.Count)})";
        }
        catch (OperationCanceledException)
        {
            var elapsed = DateTime.Now - generateStarted;
            Log.Information("Generování zrušeno uživatelem po {Elapsed}", elapsed.ToString(@"mm\:ss\.ff"));
            GenerationStatus = "Zrušeno";
        }
        catch (Exception ex)
        {
            var elapsed = DateTime.Now - generateStarted;
            var msg = ex.Message;
            if (msg.Contains("clip input is invalid", StringComparison.OrdinalIgnoreCase))
            {
                var isFluxModel = ComfyWorkflowBuilder.IsFluxModel(SelectedModel);
                GenerationStatus = isFluxModel
                    ? "Model neobsahuje CLIP/VAE — stáhni kompletní FLUX checkpoint (ne jen UNet), nebo použij GGUF soubor se samostatnými clip_l + t5xxl + ae soubory."
                    : "Model neobsahuje CLIP — ověř, že jde o kompletní checkpoint soubor (ne jen UNet/transformer).";
                Log.Warning("Generování selhalo (CLIP error) — model={Model}, trvalo={Elapsed}: {Msg}",
                            SelectedModel, elapsed.ToString(@"mm\:ss\.ff"), msg);
            }
            else
            {
                GenerationStatus = $"Chyba: {msg}";
                Log.Warning("Generování selhalo — model={Model}, trvalo={Elapsed}: {Msg}",
                            SelectedModel, elapsed.ToString(@"mm\:ss\.ff"), msg);
            }
        }
        finally
        {
            IsGenerating = false;
            _genCts      = null;
        }
    }

    [RelayCommand]
    private void StopGeneration()
    {
        if (_genCts is null) return;
        Log.Information("Uživatel zrušil generování (model={Model})", SelectedModel);
        _genCts.Cancel();
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

    [RelayCommand]
    private void ClearReferenceImage(string path)
    {
        ReferenceImagePaths.Remove(path);
    }

    [RelayCommand]
    private void ClearAllReferenceImages()
    {
        ReferenceImagePaths.Clear();
    }

    // ── LoRA příkazy ──────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task LoadLorasAsync()
    {
        // Delegace na ILoraLibraryService — kombinuje ComfyUI seznam + lokální sken,
        // vrací deduplikovaný a seřazený list (forward-slash format pro ComfyUI).
        var combined = await _loraLibrary.ListAllAsync();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AvailableLoras.Clear();
            foreach (var l in combined)
                AvailableLoras.Add(l);
        });
    }

    [RelayCommand]
    private void AddLora()
    {
        if (string.IsNullOrEmpty(SelectedLoraToAdd)) return;
        if (SelectedLoras.Any(l => l.Name == SelectedLoraToAdd)) return;
        SelectedLoras.Add(new LoraItem(SelectedLoraToAdd, LoraStrength, LoraStrength));
    }

    [RelayCommand]
    private void RemoveLora(LoraItem lora) => SelectedLoras.Remove(lora);

    [RelayCommand]
    private void ClearLoras() => SelectedLoras.Clear();

    /// <summary>Hromadné přidání cest z drag &amp; drop nebo jiného externího zdroje.</summary>
    public void AddReferenceImages(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (!string.IsNullOrEmpty(p) && !ReferenceImagePaths.Contains(p))
                ReferenceImagePaths.Add(p);
    }

    [RelayCommand]
    private async Task PickReferenceImageAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;

        var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Přidat referenční obrázky",
            AllowMultiple  = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Obrázky") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" } }
            }
        });

        if (files is { Count: > 0 })
        {
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path is not null && !ReferenceImagePaths.Contains(path))
                    ReferenceImagePaths.Add(path);
            }
        }
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

    /// <summary>
    /// Naplní GeneratedImages první stránkou záznamů z DB (volá se při startu).
    /// Zbylé stránky se dotahují přes <see cref="LoadMoreImagesAsync"/>.
    /// </summary>
    public async Task LoadSavedImagesAsync()
    {
        try
        {
            var total   = await _imageRepo.CountImagesAsync(ImagesOnlyFilter);
            var records = await _imageRepo.LoadImagesPagedAsync(skip: 0, take: GalleryPageSize, ImagesOnlyFilter);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                GeneratedImages.Clear();
                AppendRecords(records);
                TotalImagesInDb = total;

                LatestImage = GeneratedImages.Count > 0 ? GeneratedImages[0] : null;
                OnPropertyChanged(nameof(HasGeneratedImages));
                OnPropertyChanged(nameof(GalleryStatusLine));
                OnPropertyChanged(nameof(CanLoadMoreImages));
            });

            Log.Information("Galerie: načtena první stránka ({Page}/{Total} obrázků)",
                            GeneratedImages.Count, total);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load saved images from DB");
        }
    }

    /// <summary>
    /// Doplní galerii o další stránku (skip = aktuálně zobrazeno, take = GalleryPageSize).
    /// Triggered uživatelem přes tlačítko „Načíst další" v UI.
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreImagesAsync()
    {
        if (IsLoadingMoreImages || !CanLoadMoreImages) return;

        IsLoadingMoreImages = true;
        try
        {
            var skip    = GeneratedImages.Count;
            var records = await _imageRepo.LoadImagesPagedAsync(skip, GalleryPageSize, ImagesOnlyFilter);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                AppendRecords(records);
                OnPropertyChanged(nameof(GalleryStatusLine));
                OnPropertyChanged(nameof(CanLoadMoreImages));
            });

            Log.Debug("Galerie: doplněna další stránka ({N} obrázků, total v UI {Total})",
                      records.Count, GeneratedImages.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Galerie: LoadMore selhal");
        }
        finally
        {
            IsLoadingMoreImages = false;
        }
    }

    /// <summary>
    /// Mapuje <see cref="ImageRecord"/> → <see cref="GeneratedImageViewModel"/>
    /// a přidá do <see cref="GeneratedImages"/>. Záznamy se smazaným souborem
    /// na disku přeskakuje.
    /// </summary>
    private void AppendRecords(IReadOnlyList<ImageRecord> records)
    {
        foreach (var rec in records)
        {
            if (!File.Exists(rec.FilePath)) continue;

            GeneratedImages.Add(new GeneratedImageViewModel
            {
                FilePath  = rec.FilePath,
                Prompt    = rec.Prompt,
                Model     = rec.ModelName,
                Seed      = rec.Seed,
                Width     = rec.Width,
                Height    = rec.Height,
                Sampler   = rec.Sampler,
                Scheduler = rec.Scheduler,
                Steps     = rec.Steps,
                Cfg       = rec.Cfg,
                Timestamp = rec.GeneratedAt,
            });
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
    /// Resolve uživatelské Models cesty (nebo default AppData). Používá se v
    /// několika místech pre-flight kontrol — vyextrahováno aby se neopakovalo.
    /// </summary>
    private string ResolveModelsDir() =>
        string.IsNullOrWhiteSpace(_settings.Settings.ModelsDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                           "AIStudio", "Models")
            : _settings.Settings.ModelsDirectory;

    /// <summary>
    /// České skloňování slova „obrázek" podle počtu:
    /// 1 → obrázek, 2-4 → obrázky, 0 a 5+ → obrázků.
    /// </summary>
    private static string PluralizeImages(int count) => count switch
    {
        1          => "obrázek",
        >= 2 and <= 4 => "obrázky",
        _          => "obrázků",
    };
}
