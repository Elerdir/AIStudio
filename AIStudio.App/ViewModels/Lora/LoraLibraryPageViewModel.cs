using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Services;
using AIStudio.Infrastructure.Services;

namespace AIStudio.App.ViewModels.Lora;

/// <summary>
/// Knihovna lokálně stažených LoRA adapterů. MVP scope:
/// <list type="bullet">
/// <item>Sken složek <c>Models/loras/</c> a <c>Models/lora/</c> (oba názvy
/// existují, ComfyUI default je <c>loras</c>).</item>
/// <item>Filtr/search podle názvu (textbox).</item>
/// <item>Akce per LoRA: <b>Reveal</b> v Exploreru, <b>Smazat</b> (s inline confirm).</item>
/// <item>Hlavička: celkový počet + celková velikost na disku, <b>Refresh</b>,
/// <b>Otevřít složku</b>.</item>
/// </list>
///
/// <para>V další iteraci přidáme: thumbnail/preview obrázek (auto-stažený z Civitai
/// podle SHA-256), browser doporučených LoRA, drag & drop pro import .safetensors
/// z Downloads, manuální assign metadata (trigger words, base model SDXL/FLUX/SD15).</para>
/// </summary>
public partial class LoraLibraryPageViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;

    // ── Tab switcher (0 = Knihovna, 1 = Trénovat) ─────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryTab), nameof(IsTrainingTab))]
    private int _selectedTabIndex;

    public bool IsLibraryTab  => SelectedTabIndex == 0;
    public bool IsTrainingTab => SelectedTabIndex == 1;

    [RelayCommand] private void SelectLibraryTab()  => SelectedTabIndex = 0;
    [RelayCommand] private void SelectTrainingTab() => SelectedTabIndex = 1;

    /// <summary>Trénovací panel — null pokud DI neposkytl trénovací služby (degraded mode).</summary>
    public LoraTrainingPaneViewModel? TrainingPane { get; }

    /// <summary>Podporované přípony LoRA souborů (stejné jako <c>LoraLibraryService.LoraExtensions</c>).</summary>
    private static readonly string[] LoraExtensions = { ".safetensors", ".pt", ".ckpt" };

    /// <summary>Subadresáře v Models root, ve kterých hledáme LoRA.</summary>
    private static readonly string[] LoraSubdirs = { "loras", "lora" };

    /// <summary>Všechny naskenované LoRA — zdroj pro <see cref="FilteredItems"/>.</summary>
    public ObservableCollection<LoraItemViewModel> Items { get; } = new();

    /// <summary>Filtrovaný pohled na <see cref="Items"/> podle <see cref="SearchText"/>.</summary>
    public ObservableCollection<LoraItemViewModel> FilteredItems { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoItems), nameof(HasNoMatches))]
    private string _searchText = string.Empty;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>Souhrnný popis pro hlavičku (např. „24 LoRA · 8,3 GB").</summary>
    [ObservableProperty] private string _summaryLabel = string.Empty;

    /// <summary>Cesta ke složce loras — zobrazená v UI pod hlavičkou.</summary>
    [ObservableProperty] private string _lorasDirectoryPath = string.Empty;

    /// <summary>True když není naskenovaný ani jeden soubor (prázdná knihovna).</summary>
    public bool HasNoItems   => Items.Count == 0 && !IsLoading;

    /// <summary>True když máme items, ale filter žádné nevrátil — pro empty state v search výsledcích.</summary>
    public bool HasNoMatches => Items.Count > 0 && FilteredItems.Count == 0;

    /// <summary>Indikuje které LoRA má aktuálně otevřený delete-confirm panel.</summary>
    [ObservableProperty] private LoraItemViewModel? _pendingDelete;

    public LoraLibraryPageViewModel(
        ISettingsService                settings,
        ILoraTrainerService?            trainer         = null,
        ILoraTrainerDependencyService?  trainerDeps     = null,
        ISystemMonitorService?          monitor         = null,
        ILoraCaptionService?            captionService  = null,
        IDownloadService?               downloadService = null)
    {
        _settings = settings;

        // Trénovací pane je optional — pokud DI neposkytl trainer (např. headless test),
        // tab „Trénovat" zůstane skrytý.
        if (trainer is not null && trainerDeps is not null)
            TrainingPane = new LoraTrainingPaneViewModel(
                trainer, trainerDeps, settings, monitor, captionService, downloadService);

        // První načtení — fire-and-forget, UI bude zatím prázdné (IsLoading=true)
        _ = LoadAsync();
    }

    /// <summary>Naskenuje filesystem a naplní <see cref="Items"/>. Bezpečné volat opakovaně.</summary>
    [RelayCommand]
    public async Task RefreshAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        IsLoading       = true;
        StatusMessage   = "Načítám LoRA…";
        PendingDelete   = null;

        try
        {
            var modelsRoot   = AppPaths.ResolveModelsDirectory(_settings.Settings.ModelsDirectory);
            LorasDirectoryPath = ResolvePrimaryLorasDir(modelsRoot);

            var scanned = await Task.Run(() => ScanLorasInternal(modelsRoot));

            Dispatcher.UIThread.Post(() =>
            {
                Items.Clear();
                foreach (var item in scanned.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                    Items.Add(item);

                UpdateSummary();
                ApplyFilter();
                StatusMessage = string.Empty;
                OnPropertyChanged(nameof(HasNoItems));
                OnPropertyChanged(nameof(HasNoMatches));
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoraLibraryPage: scan selhal");
            StatusMessage = $"❌ Načítání selhalo: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasNoItems));
        }
    }

    /// <summary>Sken filesystému — synchronní, voláme z Task.Run kvůli UI thread.</summary>
    private List<LoraItemViewModel> ScanLorasInternal(string modelsRoot)
    {
        var result = new List<LoraItemViewModel>();
        if (string.IsNullOrWhiteSpace(modelsRoot)) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var subdir in LoraSubdirs)
        {
            var dir = Path.Combine(modelsRoot, subdir);
            if (!Directory.Exists(dir)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "LoraLibraryPage: enumerace {Dir} selhala", dir);
                continue;
            }

            foreach (var path in files)
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (!LoraExtensions.Contains(ext)) continue;

                // ComfyUI LoraLoader format: forward slashy, relativní k loras root
                var relName = Path.GetRelativePath(dir, path).Replace('\\', '/');
                if (!seen.Add(relName)) continue;  // duplicate (oba loras+lora)

                long size = 0;
                try { size = new FileInfo(path).Length; } catch { /* nelze zjistit — ukáže 0 */ }

                result.Add(new LoraItemViewModel(relName, path, size, OnDeleteRequestedAsync));
            }
        }

        return result;
    }

    /// <summary>Vrátí cestu primárního loras adresáře pro UI — existující má přednost před <c>loras</c>.</summary>
    private static string ResolvePrimaryLorasDir(string modelsRoot)
    {
        if (string.IsNullOrWhiteSpace(modelsRoot)) return string.Empty;

        var candidates = LoraSubdirs.Select(s => Path.Combine(modelsRoot, s)).ToArray();
        var existing = candidates.FirstOrDefault(Directory.Exists);
        return existing ?? candidates[0];  // default „loras" pokud nic neexistuje
    }

    private void UpdateSummary()
    {
        if (Items.Count == 0)
        {
            SummaryLabel = "Žádné LoRA";
            return;
        }

        var totalBytes = Items.Sum(i => i.SizeBytes);
        var totalLabel = totalBytes switch
        {
            < 1024L * 1024 * 1024 => $"{totalBytes / (1024.0 * 1024):F0} MB",
            _                     => $"{totalBytes / (1024.0 * 1024 * 1024):F1} GB",
        };
        SummaryLabel = $"{Items.Count} LoRA · {totalLabel}";
    }

    // ── Search / filter ───────────────────────────────────────────────────────

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredItems.Clear();
        var q = SearchText?.Trim() ?? string.Empty;

        IEnumerable<LoraItemViewModel> source = Items;
        if (!string.IsNullOrEmpty(q))
        {
            source = Items.Where(i =>
                i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                i.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in source) FilteredItems.Add(item);
        OnPropertyChanged(nameof(HasNoMatches));
    }

    // ── Akce hlavičky ─────────────────────────────────────────────────────────

    /// <summary>Otevře celou složku Models/loras v default file manageru.</summary>
    [RelayCommand]
    private void OpenLorasFolder()
    {
        if (!string.IsNullOrEmpty(LorasDirectoryPath))
        {
            // Pokud složka neexistuje, vytvoříme ji — uživatel si chce otevřít kam
            // si stahovat LoRA, není fér ho posílat do neexistující cesty.
            try
            {
                if (!Directory.Exists(LorasDirectoryPath))
                    Directory.CreateDirectory(LorasDirectoryPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "LoraLibraryPage: nelze vytvořit {Dir}", LorasDirectoryPath);
            }

            PlatformShell.Open(LorasDirectoryPath);
        }
    }

    // ── Delete flow s inline confirm ──────────────────────────────────────────

    /// <summary>
    /// Voláno z <see cref="LoraItemViewModel.DeleteCommand"/>. Otevře inline
    /// confirm panel — uživatel musí potvrdit přes <see cref="ConfirmDeleteCommand"/>
    /// nebo zrušit přes <see cref="CancelDeleteCommand"/>.
    /// </summary>
    private Task OnDeleteRequestedAsync(LoraItemViewModel item)
    {
        PendingDelete = item;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        var item = PendingDelete;
        if (item is null) return;

        item.IsDeleting = true;
        try
        {
            await Task.Run(() => File.Delete(item.FilePath));

            Dispatcher.UIThread.Post(() =>
            {
                Items.Remove(item);
                FilteredItems.Remove(item);
                UpdateSummary();
                OnPropertyChanged(nameof(HasNoItems));
                OnPropertyChanged(nameof(HasNoMatches));
                StatusMessage = $"Smazáno: {item.DisplayName}";
            });

            Log.Information("LoraLibraryPage: smazáno {Path}", item.FilePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoraLibraryPage: smazání {Path} selhalo", item.FilePath);
            StatusMessage = $"❌ Smazání selhalo: {ex.Message}";
        }
        finally
        {
            item.IsDeleting = false;
            PendingDelete   = null;
        }
    }

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;
}
