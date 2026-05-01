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
    private readonly IComfyService      _comfy;
    private readonly ISettingsService   _settings;
    private readonly IImageRepository   _imageRepo;
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
    [ObservableProperty] private string? _referenceImagePath;
    [ObservableProperty] private double _referenceStrength = 0.7;
    [ObservableProperty] private bool _hasReferenceImage;
    [ObservableProperty] private GeneratedImageViewModel? _latestImage;

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

    public bool HasGeneratedImages => GeneratedImages.Count > 0;

    public ImageGeneratorViewModel(
        IComfyService    comfy,
        ISettingsService settings,
        IImageRepository imageRepo)
    {
        _comfy     = comfy;
        _settings  = settings;
        _imageRepo = imageRepo;
        var n = System.Threading.Interlocked.Increment(ref _counter);
        _title = $"Generátor {n}";

        UpdateModelDefaults(SelectedModel);
    }

    // ── Property hooks ────────────────────────────────────────────────────────

    partial void OnSelectedAspectRatioChanged(AspectRatio value)
    {
        OnPropertyChanged(nameof(AspectRatioLabel));
        OnPropertyChanged(nameof(ResolutionLabel));
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

            // Workflow router:
            //   • FLUX GGUF  → UnetLoaderGGUF + DualCLIPLoader + VAELoader (4-loader workflow)
            //   • FLUX safetensors → klasický CheckpointLoaderSimple (all-in-one)
            //   • SDXL / SD  → klasický CheckpointLoaderSimple
            //
            // FLUX GGUF předpokládá, že uživatel má v Models složce CLIP-L, T5 a VAE
            // (najde je přes extra_model_paths.yaml). Pokud chybí, ComfyUI vrátí
            // validation error a my ho v UI ukážeme.
            Dictionary<string, object> workflow;
            if (isFlux && isGguf)
            {
                workflow = ComfyWorkflowBuilder.BuildFluxGguf(
                    SelectedModel,
                    ComfyWorkflowBuilder.DefaultFluxClipL,
                    ComfyWorkflowBuilder.DefaultFluxT5,
                    ComfyWorkflowBuilder.DefaultFluxVae,
                    Prompt, res.W, res.H, Steps, Cfg, seed, VariantCount);
            }
            else if (isFlux)
            {
                workflow = ComfyWorkflowBuilder.BuildFlux(
                    SelectedModel, Prompt, res.W, res.H, Steps, Cfg, seed, VariantCount);
            }
            else
            {
                workflow = ComfyWorkflowBuilder.BuildStandard(
                    SelectedModel, Prompt, NegativePrompt, res.W, res.H, Steps, Cfg, seed, VariantCount);
            }

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
    private void ClearReferenceImage()
    {
        ReferenceImagePath = null;
        HasReferenceImage  = false;
    }

    [RelayCommand]
    private async Task PickReferenceImageAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;

        var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Vybrat referenční obrázek",
            AllowMultiple  = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Obrázky") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" } }
            }
        });

        if (files is { Count: > 0 })
        {
            ReferenceImagePath = files[0].TryGetLocalPath();
            HasReferenceImage  = ReferenceImagePath is not null;
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
