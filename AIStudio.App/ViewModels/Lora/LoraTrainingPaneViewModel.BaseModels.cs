using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;
using System.Collections.ObjectModel;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AIStudio.Infrastructure.Services;

namespace AIStudio.App.ViewModels.Lora;

/// <summary>
/// Výběr base modelu — doporučené checkpointy včetně stahování, přepínač
/// SDXL/FLUX, scan lokálních modelů a ruční procházení. Partial split z hlavního
/// <see cref="LoraTrainingPaneViewModel"/>: největší a nejvíc I/O část stránky.
/// </summary>
public partial class LoraTrainingPaneViewModel
{
    // ── Doporučené base modely (s download akcí) ──────────────────────────────

    /// <summary>
    /// Curated seznam doporučených SDXL checkpointů pro trénink — jednorázově
    /// při startu naplníme z <see cref="RecommendedModels"/>. State (downloaded/
    /// downloading) se refreshuje při změně Available list (RefreshBaseModelsAsync).
    /// </summary>
    public ObservableCollection<RecommendedBaseModelViewModel> RecommendedBaseModels { get; } = new();

    /// <summary>True když máme funkční download service v DI — pro UI IsVisible.</summary>
    public bool IsDownloadSupported => _downloadService is not null;

    private void BuildRecommendedBaseModels()
    {
        // Curated picky vhodné pro LoRA trénink. Sdílíme katalog s chat → image gen
        // recommenderem (RecommendedModels.*), takže přidání modelu do katalogu se
        // automaticky promítne sem.
        // Nabízíme jen TRÉNOVATELNÉ base modely (plný safetensors). FLUX GGUF
        // (netrénovatelný) tu není — pro FLUX je tu fp8 dev safetensors. Gate
        // na GGUF je ve SdScriptsLoraTrainer.ValidateRequest.
        var picks = new[]
        {
            RecommendedModels.SdxlBase10,             // univerzální SDXL pro postavy/scény
            RecommendedModels.DreamShaperXl_Lightning, // stylizovaný/cinematic SDXL
            RecommendedModels.AnimagineXl31,          // anime SDXL
            RecommendedModels.DreamShaper8_Sd15,      // SD 1.5 — rychlejší, míň VRAM
            RecommendedModels.FluxDev_Fp8,            // FLUX dev fp8 — nejvyšší kvalita
        };

        var modelsRoot = AppPaths.ResolveModelsDirectory(_settings.Settings.ModelsDirectory);
        var ckptDir    = Path.Combine(modelsRoot, "checkpoints");

        foreach (var p in picks)
        {
            var targetPath = Path.Combine(ckptDir, p.FileName);
            var existing   = File.Exists(targetPath) || IsFileInComfyUiCheckpoints(p.FileName);
            RecommendedBaseModels.Add(new RecommendedBaseModelViewModel(
                source:              p,
                targetPath:          targetPath,
                isDownloaded:        existing,
                onDownloadRequested: OnRecommendedDownloadRequestedAsync,
                onCancelRequested:   OnRecommendedDownloadCancelAsync));
        }

        RefreshFilteredBaseModels();
    }

    // ── Přepínač typu tréninku (SDXL / FLUX) ─────────────────────────────────

    /// <summary>Dostupné typy tréninku pro segmentový přepínač.</summary>
    public IReadOnlyList<string> TrainingTypes { get; } = new[] { "SDXL", "FLUX" };

    /// <summary>
    /// Zvolený typ tréninku — řídí filtr doporučených base modelů + vodítko podle HW.
    /// Skutečný typ tréninku se stále odvozuje z vybraného base (BaseModelTypeLabel),
    /// ale přepínač uživatele nasměruje a drží se s ním v synchronu.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrainingTypeGuidance), nameof(IsSdxlType), nameof(IsFluxType))]
    private string _selectedTrainingType = "SDXL";

    public bool IsSdxlType => SelectedTrainingType == "SDXL";
    public bool IsFluxType => SelectedTrainingType == "FLUX";

    private bool _syncingTrainingType;

    /// <summary>Doporučené base modely filtrované podle zvoleného typu (SDXL skupina vs FLUX).</summary>
    public ObservableCollection<RecommendedBaseModelViewModel> FilteredRecommendedBaseModels { get; } = new();

    /// <summary>Přepne typ tréninku (segmentová tlačítka SDXL / FLUX).</summary>
    [RelayCommand]
    private void SelectTrainingType(string? type)
    {
        if (!string.IsNullOrEmpty(type)) SelectedTrainingType = type;
    }

    partial void OnSelectedTrainingTypeChanged(string value)
    {
        RefreshFilteredBaseModels();
        if (_syncingTrainingType) return;   // změna přišla ze sync base→typ, base nepřepínáme

        // Když aktuální base nesedí s typem, vyber stažený base správného typu.
        if (!TypeMatches(BaseModelTypeLabel, value))
        {
            var match = AvailableBaseModels.FirstOrDefault(m => TypeMatches(DetectType(m), value));
            if (match is not null) SelectedBaseModel = match;
        }
    }

    /// <summary>Vodítko podle typu + dostupné VRAM — pomáhá uživateli rozhodnout.</summary>
    public string TrainingTypeGuidance
    {
        get
        {
            var vram    = Math.Max(_monitor?.Current?.VramTotalGb ?? 0, _lastKnownVramGb);
            var vramTxt = vram > 0 ? $"{vram:F0} GB VRAM" : "tvé GPU";
            return SelectedTrainingType == "FLUX"
                ? $"FLUX — nejvyšší kvalita a věrnost, ale náročné. Na {vramTxt} se trénuje v rozlišení 768 s block-swapem; počítej s hodinami. CLIP/T5/VAE se stáhnou samy."
                : $"SDXL — rychlé a spolehlivé (~30–60 min na {vramTxt}), pohodlně se vejde do VRAM. Pro postavu/obličej bohatě stačí. Doporučeno, pokud nechceš čekat.";
        }
    }

    private void RefreshFilteredBaseModels()
    {
        FilteredRecommendedBaseModels.Clear();
        foreach (var r in RecommendedBaseModels)
            if (TypeMatches(r.ModelTypeLabel, SelectedTrainingType))
                FilteredRecommendedBaseModels.Add(r);
    }

    /// <summary>SDXL skupina = SDXL i SD 1.5 (lehké varianty), FLUX skupina = jen FLUX.</summary>
    private static bool TypeMatches(string modelType, string selectedType)
        => selectedType == "FLUX" ? modelType == "FLUX" : modelType != "FLUX";

    private static string DetectType(string displayOrName)
    {
        var lower = displayOrName.ToLowerInvariant();
        if (lower.Contains("flux"))                         return "FLUX";
        if (lower.Contains("xl") || lower.Contains("sdxl")) return "SDXL";
        return "SD 1.5";
    }

    /// <summary>Refreshne flag IsDownloaded u doporučených podle aktuálně přítomných checkpointů.</summary>
    private void RefreshRecommendedDownloadedFlags()
    {
        foreach (var rec in RecommendedBaseModels)
        {
            if (rec.IsDownloading) continue;   // neměníme stav v průběhu DL
            rec.IsDownloaded = File.Exists(rec.TargetPath) || IsFileInComfyUiCheckpoints(rec.Source.FileName);
        }
    }

    /// <summary>True pokud daný filename existuje v ComfyUI bundle checkpoints/.</summary>
    private bool IsFileInComfyUiCheckpoints(string fileName)
    {
        var comfyDir = _settings.Settings.ComfyUiDirectory;
        if (string.IsNullOrWhiteSpace(comfyDir)) return false;
        return File.Exists(Path.Combine(comfyDir, "models", "checkpoints", fileName));
    }

    private async Task OnRecommendedDownloadRequestedAsync(RecommendedBaseModelViewModel rec)
    {
        if (_downloadService is null) return;
        if (rec.IsDownloading || rec.IsDownloaded) return;

        var cts = new CancellationTokenSource();
        _modelDownloadCts[rec.Source.Id] = cts;

        rec.IsDownloading      = true;
        rec.DownloadProgress   = 0;
        rec.DownloadStatusLine = "Spouštím stahování…";

        var progress = new Progress<DownloadProgressInfo>(p => Dispatcher.UIThread.Post(() =>
        {
            rec.DownloadProgress   = p.Percent;
            var mbps               = p.BytesPerSecond / 1_048_576;
            rec.DownloadStatusLine = p.Total > 0
                ? $"{p.Downloaded / 1_048_576} / {p.Total / 1_048_576} MB · {mbps:F1} MB/s"
                : $"{p.Downloaded / 1_048_576} MB · {mbps:F1} MB/s";
        }));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(rec.TargetPath)!);

            var token = rec.Source.RequiresHuggingFaceToken
                ? _settings.Settings.HuggingFaceToken
                : null;

            await _downloadService.DownloadFileAsync(
                rec.Source.DownloadUrl,
                rec.TargetPath,
                progress,
                token,
                cts.Token,
                rec.Source.Sha256);

            Dispatcher.UIThread.Post(() =>
            {
                rec.IsDownloading      = false;
                rec.IsDownloaded       = true;
                rec.DownloadProgress   = 100;
                rec.DownloadStatusLine = "Hotovo";
            });

            // Refresh seznamu Available aby se nový model objevil v dropdownu;
            // a pokud nic není vybrané, auto-select tenhle.
            await RefreshBaseModelsAsync();

            Dispatcher.UIThread.Post(() =>
            {
                if (string.IsNullOrEmpty(SelectedBaseModel))
                {
                    var match = AvailableBaseModels
                        .FirstOrDefault(n => n.Contains(rec.Source.FileName, StringComparison.OrdinalIgnoreCase));
                    if (match is not null) SelectedBaseModel = match;
                }
            });

            Log.Information("LoraTrainingPane: model {Name} stažen do {Path}",
                rec.Source.Name, rec.TargetPath);
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                rec.IsDownloading      = false;
                rec.DownloadStatusLine = "Zrušeno";
            });
            try { if (File.Exists(rec.TargetPath)) File.Delete(rec.TargetPath); } catch { }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoraTrainingPane: stažení {Name} selhalo", rec.Source.Name);
            Dispatcher.UIThread.Post(() =>
            {
                rec.IsDownloading      = false;
                rec.DownloadStatusLine = $"❌ {ex.Message}";
            });
        }
        finally
        {
            _modelDownloadCts.Remove(rec.Source.Id);
            cts.Dispose();
        }
    }

    private Task OnRecommendedDownloadCancelAsync(RecommendedBaseModelViewModel rec)
    {
        if (_modelDownloadCts.TryGetValue(rec.Source.Id, out var cts))
        {
            try { cts.Cancel(); }
            catch (Exception ex) { Log.Warning(ex, "LoraTrainingPane: cancel DL selhal"); }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Otevře <c>Models/checkpoints/</c> v default file manageru — uživatel
    /// si tam může ručně zkopírovat stažený .safetensors checkpoint.
    /// Pokud složka neexistuje, vytvoříme ji.
    /// </summary>
    [RelayCommand]
    private void OpenCheckpointsFolder()
    {
        var modelsRoot = AppPaths.ResolveModelsDirectory(_settings.Settings.ModelsDirectory);
        var ckptDir    = Path.Combine(modelsRoot, "checkpoints");
        try
        {
            if (!Directory.Exists(ckptDir)) Directory.CreateDirectory(ckptDir);
            AIStudio.Infrastructure.Services.PlatformShell.Open(ckptDir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoraTrainingPane: otevření {Dir} selhalo", ckptDir);
        }
    }

    /// <summary>
    /// Naskenuje <b>všechny známé checkpoint lokace</b> a naplní seznam base modelů:
    /// <list type="bullet">
    /// <item>AI Studio Models/checkpoints/ (settings.ModelsDirectory)</item>
    /// <item>ComfyUI bundle models/checkpoints/ (settings.ComfyUiDirectory)</item>
    /// </list>
    /// Důvod: ComfyUI Portable má vlastní složku <c>models/checkpoints/</c> a uživatel
    /// tam typicky modely stáhne (Image Studio je vidí přes ComfyUI API). Pro sd-scripts
    /// trénink potřebujeme absolutní cestu — proto držíme mapování v <see cref="_baseModelPaths"/>.
    /// </summary>
    [RelayCommand]
    public async Task RefreshBaseModelsAsync()
    {
        var settings   = _settings.Settings;
        var aiModelsDir = AppPaths.ResolveModelsDirectory(settings.ModelsDirectory);

        // Lokace, ve kterých hledáme — (display prefix, absolutní path).
        // checkpoints/ = SD/SDXL; unet/ + diffusion_models/ = FLUX UNET modely
        // (FLUX LoRA trénink na nich, vč. flux1-dev / kontext fp8).
        var scanLocations = new List<(string Label, string Dir)>
        {
            ("AI Studio", Path.Combine(aiModelsDir, "checkpoints")),
            ("AI Studio", Path.Combine(aiModelsDir, "unet")),
            ("AI Studio", Path.Combine(aiModelsDir, "diffusion_models")),
        };

        // ComfyUI lokace — pokud máme nastavený directory, hledáme tam taky.
        // ComfyUI Portable má pevnou strukturu {ComfyUiDir}/models/{checkpoints,unet,…}.
        if (!string.IsNullOrWhiteSpace(settings.ComfyUiDirectory))
        {
            var comfyModels = Path.Combine(settings.ComfyUiDirectory, "models");
            scanLocations.Add(("ComfyUI", Path.Combine(comfyModels, "checkpoints")));
            scanLocations.Add(("ComfyUI", Path.Combine(comfyModels, "unet")));
            scanLocations.Add(("ComfyUI", Path.Combine(comfyModels, "diffusion_models")));
        }

        var found = await Task.Run(() =>
        {
            var results = new List<(string Display, string FullPath)>();
            foreach (var (label, dir) in scanLocations)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var ext in new[] { "*.safetensors", "*.ckpt" })
                    foreach (var path in Directory.EnumerateFiles(dir, ext, SearchOption.AllDirectories))
                    {
                        var relName = Path.GetRelativePath(dir, path).Replace('\\', '/');
                        // Pokud máme víc lokací, prefixujeme display názvem zdroje pro
                        // rozlišení duplicit (uživatel může mít stejný model na obou
                        // místech — ukážeme oba ať si vybere).
                        var display = scanLocations.Count > 1
                            ? $"[{label}] {relName}"
                            : relName;
                        results.Add((display, path));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "LoraTrainingPane: scan {Dir} selhal", dir);
                }
            }
            return results
                .OrderBy(r => r.Display, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });

        Dispatcher.UIThread.Post(() =>
        {
            AvailableBaseModels.Clear();
            _baseModelPaths.Clear();

            foreach (var (display, fullPath) in found)
            {
                AvailableBaseModels.Add(display);
                _baseModelPaths[display] = fullPath;
            }

            // Auto-select první stažený, pokud žádný není vybraný
            if (string.IsNullOrEmpty(SelectedBaseModel) && AvailableBaseModels.Count > 0)
                SelectedBaseModel = AvailableBaseModels[0];

            // Sync „Staženo" badge u doporučených karet — soubor mohl nově přibýt
            // (DL dokončen, externí kopie, atd.)
            RefreshRecommendedDownloadedFlags();
        });
    }

    /// <summary>
    /// Otevře file picker a přidá ručně zvolený checkpoint do seznamu. Užitečné když
    /// uživatel má model na netradiční cestě (např. externí disk) a nechce ho
    /// přesouvat. Vybraný soubor se přidá s prefixem [Vlastní] a hned se zvolí.
    /// </summary>
    [RelayCommand]
    private async Task BrowseBaseModelAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;

        var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Vyber základní model (.safetensors / .ckpt)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Checkpoint modely") { Patterns = new[] { "*.safetensors", "*.ckpt" } }
            }
        });

        if (files.Count == 0) return;
        var path = files[0].Path.LocalPath;
        if (!File.Exists(path)) return;

        var display = $"[Vlastní] {Path.GetFileName(path)}";

        Dispatcher.UIThread.Post(() =>
        {
            // Pokud už tam je (uživatel vybral podruhé stejný), jen vyber
            if (!AvailableBaseModels.Contains(display))
            {
                AvailableBaseModels.Add(display);
                _baseModelPaths[display] = path;
            }
            SelectedBaseModel = display;
        });
    }

    /// <summary>
    /// Vrátí absolutní cestu k vybranému checkpointu. Pokud nic nevybráno
    /// nebo se cesta v mapování neztratila, vrátí null.
    /// </summary>
    private string? ResolveSelectedBaseModelPath()
    {
        if (string.IsNullOrEmpty(SelectedBaseModel)) return null;
        return _baseModelPaths.TryGetValue(SelectedBaseModel, out var path) ? path : null;
    }

}
