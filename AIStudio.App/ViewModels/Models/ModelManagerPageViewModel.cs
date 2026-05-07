using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels.Models;

/// <summary>
/// Spravuje záložky <b>Doporučené / Hledat / Stažené</b>. Místo hardcoded
/// katalogu modelů používá <see cref="IModelDiscoveryService"/>, který tahá
/// živá data z HuggingFace + Civitai a cachuje je 24 h.
///
/// Struktura tabů:
/// • Tab 0 (Doporučené) — sekce z <c>discovery.Picks</c>, každá lazy-loadnutá.
/// • Tab 1 (Hledat)     — free-form search napříč HF + Civitai s filtry.
/// • Tab 2 (Stažené)    — sken lokálního Models/ adresáře.
/// </summary>
public partial class ModelManagerPageViewModel : ViewModelBase
{
    private readonly IDownloadService          _downloader;
    private readonly ISettingsService          _settings;
    private readonly ILlamaService             _llama;
    private readonly IHuggingFaceClient        _hf;
    private readonly IModelDiscoveryService    _discovery;

    // ── Hlavní stav UI ────────────────────────────────────────────────────────

    /// <summary>0 = Doporučené, 1 = Hledat, 2 = Stažené.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecommendedTab), nameof(IsSearchTab),
                              nameof(IsDownloadedTab), nameof(IsRecommendedTabActive),
                              nameof(IsSearchTabActive), nameof(IsDownloadedTabActive))]
    private int _selectedTabIndex;

    /// <summary>
    /// Bool projekce SelectedTabIndex pro IsVisible bindingy. Komparátor přes
    /// ObjectConverters.Equal s int vs string ConverterParameter v Avalonia 12
    /// + compiled bindings selhává tiše (žádný error log, jen IsVisible=false).
    /// Tyto computed properties to obejdou.
    /// </summary>
    public bool IsRecommendedTab => SelectedTabIndex == 0;
    public bool IsSearchTab      => SelectedTabIndex == 1;
    public bool IsDownloadedTab  => SelectedTabIndex == 2;

    // Aliasy pro tab buttons (Classes.activeTab) — stejná logika, jiné jméno
    // pro čitelnost, kdyby v budoucnu chtěli mít jiné podmínky.
    public bool IsRecommendedTabActive => IsRecommendedTab;
    public bool IsSearchTabActive      => IsSearchTab;
    public bool IsDownloadedTabActive  => IsDownloadedTab;

    /// <summary>True když SearchResults je prázdné — pro empty state v tabu Hledat.</summary>
    public bool HasNoSearchResults    => SearchResults.Count == 0;

    /// <summary>True když DownloadedModels je prázdné — pro empty state v tabu Stažené.</summary>
    public bool HasNoDownloadedModels => DownloadedModels.Count == 0;

    [ObservableProperty] private ModelItemViewModel? _selectedModel;

    /// <summary>Sekce v tabu „Doporučené" — instance pro každý <see cref="ModelPick"/>.</summary>
    public ObservableCollection<RecommendedSectionViewModel> RecommendedSections { get; } = new();

    /// <summary>Výsledky tabu „Hledat".</summary>
    public ObservableCollection<ModelItemViewModel> SearchResults { get; } = new();

    /// <summary>Lokálně stažené modely v tabu „Stažené".</summary>
    public ObservableCollection<ModelItemViewModel> DownloadedModels { get; } = new();

    /// <summary>True dokud se „Doporučené" načítá poprvé — UI ukáže globální spinner.</summary>
    [ObservableProperty] private bool _isLoadingRecommended;

    /// <summary>True jakmile uživatel poprvé otevře „Doporučené" — zabraňuje opakovanému loadu.</summary>
    private bool _recommendedLoaded;

    // ── Search tab ────────────────────────────────────────────────────────────

    [ObservableProperty] private string _searchQuery   = string.Empty;
    [ObservableProperty] private bool   _isSearching;
    [ObservableProperty] private string _searchError   = string.Empty;
    [ObservableProperty] private bool   _includeNsfw;

    /// <summary>0 = Obojí, 1 = HuggingFace, 2 = Civitai. Pro HF nemá smysl NSFW (safe).</summary>
    [ObservableProperty] private int _searchProviderIndex;

    /// <summary>0 = Vše, 1 = Chat, 2 = Image (checkpoint), 3 = LoRA.</summary>
    [ObservableProperty] private int _searchKindIndex;

    // ── HF file picker (po vybrání HF modelu z doporučených/search) ───────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHfFiles))]
    private bool _isLoadingHfFiles;

    public ObservableCollection<HfFileInfoVm> HfFiles { get; } = new();
    public bool HasHfFiles => HfFiles.Count > 0;

    // ── Download tracking ─────────────────────────────────────────────────────

    private readonly Dictionary<ModelItemViewModel, CancellationTokenSource> _activeDownloads = new();

    // ── Path resolution ───────────────────────────────────────────────────────

    private string ModelsDir
    {
        get
        {
            var custom = _settings.Settings.ModelsDirectory;
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                               "AIStudio", "Models")
                : custom;
        }
    }

    public ModelManagerPageViewModel(
        IDownloadService       downloader,
        ISettingsService       settings,
        ILlamaService          llama,
        IHuggingFaceClient     hf,
        IModelDiscoveryService discovery)
    {
        _downloader = downloader;
        _settings   = settings;
        _llama      = llama;
        _hf         = hf;
        _discovery  = discovery;

        // Sekce vyrobíme hned (z metadat Picks) — modely se naplní lazy.
        foreach (var pick in _discovery.Picks)
            RecommendedSections.Add(new RecommendedSectionViewModel(pick));

        // Notifikace prázdného stavu v tabech Hledat/Stažené — ObservableCollection
        // sám PropertyChanged nefiruje, ale CollectionChanged fíruje při Add/Clear.
        SearchResults.CollectionChanged    += (_, _) => OnPropertyChanged(nameof(HasNoSearchResults));
        DownloadedModels.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoDownloadedModels));

        // Sken disku + load doporučených na pozadí — všechno v Task.Run, aby UI
        // thread mohl rovnou vykreslit page bez čekání na I/O. Bez Task.Run
        // by konstruktor blokoval na velkém Models/ adresáři.
        _recommendedLoaded = true;
        _ = Task.Run(async () =>
        {
            try { ScanLocalModelsIntoDownloaded(); }
            catch (Exception ex) { Log.Warning(ex, "ModelManager: úvodní sken selhal"); }

            try { await LoadRecommendedAsync(); }
            catch (Exception ex) { Log.Warning(ex, "ModelManager: úvodní LoadRecommended selhal"); }
        });
    }

    // ── Tab switching ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SelectTabAsync(string index)
    {
        if (!int.TryParse(index, out var i)) return;
        SelectedTabIndex = i;

        // Lazy load: doporučené poprvé až když uživatel reálně tab otevře —
        // ušetří startup lag a šetří API requesty u uživatelů, kteří zůstanou
        // u stažených.
        if (i == 0 && !_recommendedLoaded)
        {
            _recommendedLoaded = true;
            await LoadRecommendedAsync();
        }
    }

    // ── Tab 0: Doporučené ─────────────────────────────────────────────────────

    [RelayCommand]
    public async Task LoadRecommendedAsync()
    {
        IsLoadingRecommended = true;
        try
        {
            // Sekce načítáme paralelně — každá má vlastní spinner, takže UI se
            // postupně plní. Nečekáme na všechny, ale držíme globální flag,
            // dokud poslední sekce nedoběhne.
            //
            // Metoda LoadSectionAsync má default parametr bypassCache=false, což
            // brání method-group-to-delegate konverzi (default params nejsou
            // součástí signatury, jen call-site features). Proto explicitní lambda.
            var tasks = RecommendedSections.Select(s => LoadSectionAsync(s, false));
            await Task.WhenAll(tasks);
        }
        finally
        {
            IsLoadingRecommended = false;
        }
    }

    [RelayCommand]
    public async Task RefreshRecommendedAsync()
    {
        IsLoadingRecommended = true;
        try
        {
            var tasks = RecommendedSections.Select(s => LoadSectionAsync(s, bypassCache: true));
            await Task.WhenAll(tasks);
        }
        finally
        {
            IsLoadingRecommended = false;
        }
    }

    private async Task LoadSectionAsync(RecommendedSectionViewModel section, bool bypassCache = false)
    {
        Log.Information("ModelManager: load section '{Id}' start", section.Id);
        section.IsLoading = true;
        section.LoadError = string.Empty;
        try
        {
            var models = await _discovery.FetchPickAsync(section.Pick, bypassCache);
            Log.Information("ModelManager: section '{Id}' fetched {N} modelů, dispatching to UI",
                            section.Id, models.Count);

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    section.Models.Clear();
                    foreach (var m in models)
                        section.Models.Add(BuildItemFromDiscovered(m, section.Kind));
                    section.HasLoaded = true;
                    MergeDownloadedFlagsInto(section.Models);
                    Log.Information("ModelManager: section '{Id}' Models naplněno ({N})",
                                    section.Id, section.Models.Count);
                }
                catch (Exception postEx)
                {
                    Log.Error(postEx, "ModelManager: section '{Id}' UI dispatch FAILED", section.Id);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ModelManager: section '{Id}' load selhal", section.Id);
            section.LoadError = "Nepodařilo se načíst — zkontroluj internet a tokeny.";
            section.HasLoaded = true;
        }
        finally
        {
            section.IsLoading = false;
        }
    }

    // ── Tab 1: Hledat ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RunSearchAsync()
    {
        if (IsSearching || string.IsNullOrWhiteSpace(SearchQuery)) return;

        IsSearching  = true;
        SearchError  = string.Empty;
        SearchResults.Clear();

        try
        {
            // Provider mapping: 0 = Obojí, 1 = HF, 2 = Civitai.
            // Pro „Obojí" zaspustíme oba paralelně a slijeme výsledky.
            var kind = SearchKindIndex switch
            {
                1 => (PickKind?)PickKind.Chat,
                2 => PickKind.Image,
                3 => PickKind.Lora,
                _ => null
            };

            var query = SearchQuery.Trim();
            var hfTask  = SearchProviderIndex is 0 or 1
                ? _discovery.SearchAsync(PickProvider.HuggingFace, query, kind, false, 20)
                : Task.FromResult<IReadOnlyList<DiscoveredModel>>(Array.Empty<DiscoveredModel>());
            var civTask = SearchProviderIndex is 0 or 2
                ? _discovery.SearchAsync(PickProvider.Civitai, query, kind, IncludeNsfw, 20)
                : Task.FromResult<IReadOnlyList<DiscoveredModel>>(Array.Empty<DiscoveredModel>());

            await Task.WhenAll(hfTask, civTask);

            // Pořadí: civitai napřed pro image hledání (lépe popsáno), HF napřed
            // pro chat (víc downloads). Když jsou oba, prokládáme.
            var combined = (kind == PickKind.Chat ? hfTask.Result.Concat(civTask.Result)
                                                  : civTask.Result.Concat(hfTask.Result))
                           .ToList();

            Dispatcher.UIThread.Post(() =>
            {
                SearchResults.Clear();
                foreach (var d in combined)
                {
                    var item = BuildItemFromDiscovered(d, kind ?? GuessKindFromProvider(d));
                    SearchResults.Add(item);
                }
                MergeDownloadedFlagsInto(SearchResults);
            });
        }
        catch (Exception ex)
        {
            SearchError = $"Vyhledávání selhalo: {ex.Message}";
            Log.Warning(ex, "ModelManager: search '{Q}' selhal", SearchQuery);
        }
        finally
        {
            IsSearching = false;
        }
    }

    private static PickKind GuessKindFromProvider(DiscoveredModel d) =>
        d.FileFormat == "GGUF" ? PickKind.Chat : PickKind.Image;

    // ── Selection + HF file picker ────────────────────────────────────────────

    [RelayCommand]
    private async Task SelectModelAsync(ModelItemViewModel model)
    {
        if (model is null) return;

        if (SelectedModel is not null && !ReferenceEquals(SelectedModel, model))
            SelectedModel.IsSelected = false;
        model.IsSelected = true;
        SelectedModel = model;

        // Pro HF položky odhalíme dostupné GGUF varianty v detailu —
        // Civitai už má přímý DownloadUrl a FileName, takže není co rozbalovat.
        HfFiles.Clear();
        if (model.Source == ModelSource.HuggingFace &&
            !string.IsNullOrWhiteSpace(model.ProviderRef))
        {
            await LoadHfFilesAsync(model.ProviderRef);
        }
        OnPropertyChanged(nameof(HasHfFiles));
    }

    private async Task LoadHfFilesAsync(string repoId)
    {
        IsLoadingHfFiles = true;
        try
        {
            var files = await _hf.ListGgufFilesAsync(repoId);
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var f in files)
                    HfFiles.Add(new HfFileInfoVm(f, repoId));
                OnPropertyChanged(nameof(HasHfFiles));
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ModelManager: ListGgufFiles '{Repo}' selhal", repoId);
        }
        finally
        {
            IsLoadingHfFiles = false;
        }
    }

    [RelayCommand]
    private async Task DownloadHfFileAsync(HfFileInfoVm file)
    {
        if (file is null || file.IsDownloading || file.IsDownloaded) return;
        if (SelectedModel is null) return;

        // Recyklujeme stávající download flow — vyrobíme spec ModelItem pro
        // konkrétní soubor, spustíme DownloadAsync, a pak ho přidáme do
        // DownloadedModels (sken adresáře by ho stejně našel).
        var displayName = SelectedModel.Name;
        var item = new ModelItemViewModel
        {
            Name           = $"{displayName} · {Path.GetFileNameWithoutExtension(file.Path)}",
            Description    = SelectedModel.Description,
            FileName       = file.Path,
            Category       = SelectedModel.Category,
            Source         = ModelSource.HuggingFace,
            ProviderRef    = SelectedModel.ProviderRef,
            Quantization   = ExtractQuantizationFromName(file.Path),
            DownloadUrl    = _hf.BuildDownloadUrl(SelectedModel.ProviderRef, file.Path),
            ModelPageUrl   = SelectedModel.ModelPageUrl,
            Size           = FormatFileSize(file.SizeBytes),
            VramRequiredGb = (int)Math.Ceiling(file.SizeBytes / 1_073_741_824.0 * 1.3),
        };

        file.IsDownloading = true;
        try
        {
            await DownloadAsync(item);
            file.IsDownloaded = item.IsDownloaded;

            if (item.IsDownloaded)
            {
                Dispatcher.UIThread.Post(() => DownloadedModels.Add(item));
            }
        }
        finally
        {
            file.IsDownloading = false;
        }
    }

    /// <summary>Vytáhne kvantizaci z file name, např. "model-Q4_K_M.gguf" → "Q4_K_M".</summary>
    private static string ExtractQuantizationFromName(string fileName)
    {
        var noExt = Path.GetFileNameWithoutExtension(fileName);
        var dash = noExt.LastIndexOf('-');
        if (dash >= 0 && dash < noExt.Length - 1)
        {
            var tail = noExt[(dash + 1)..];
            if (tail.StartsWith('Q') || tail.StartsWith('F') || tail.StartsWith('B'))
                return tail.ToUpperInvariant();
        }
        return string.Empty;
    }

    // ── Download / Cancel / Set active / Open / Delete ────────────────────────

    [RelayCommand]
    private async Task DownloadAsync(ModelItemViewModel model)
    {
        if (model.IsDownloading) return;
        if (string.IsNullOrEmpty(model.DownloadUrl))
        {
            // HF položky mají prázdný DownloadUrl — uživatel musí vybrat soubor
            // v sekci „Dostupné varianty".
            model.DownloadError = "Vyber konkrétní soubor (kvantizaci) v detailu.";
            return;
        }

        var token = model.Source == ModelSource.Civitai
            ? _settings.Settings.CivitaiApiKey
            : _settings.Settings.HuggingFaceToken;

        if (model.Source == ModelSource.Civitai && string.IsNullOrWhiteSpace(token))
        {
            model.DownloadError =
                "Civitai vyžaduje API klíč. Nastav ho v Nastavení → Stahování.";
            return;
        }

        var destPath = Path.Combine(ModelsDir, model.FileName);
        var cts = new CancellationTokenSource();
        _activeDownloads[model] = cts;

        model.DownloadError    = string.Empty;
        model.IsDownloading    = true;
        model.DownloadProgress = 0;
        model.DownloadedBytes  = 0;
        model.TotalBytes       = 0;

        var progress = new Progress<DownloadProgressInfo>(info =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                model.DownloadedBytes          = info.Downloaded;
                model.TotalBytes               = info.Total;
                model.DownloadProgress         = info.Percent;
                model.DownloadSpeedBytesPerSec = info.BytesPerSecond;
            });
        });

        try
        {
            Directory.CreateDirectory(ModelsDir);
            await _downloader.DownloadFileAsync(model.DownloadUrl, destPath, progress, token, cts.Token);

            Dispatcher.UIThread.Post(() =>
            {
                model.IsDownloaded             = true;
                model.IsDownloading            = false;
                model.DownloadProgress         = 100;
                model.DownloadSpeedBytesPerSec = 0;
                model.SizeOnDisk               = FormatFileSize(new FileInfo(destPath).Length);
                _activeDownloads.Remove(model);

                _settings.NotifyModelLibraryChanged();
                RefreshDownloadedTab();
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                model.IsDownloading            = false;
                model.DownloadProgress         = 0;
                model.DownloadedBytes          = 0;
                model.TotalBytes               = 0;
                model.DownloadSpeedBytesPerSec = 0;
                _activeDownloads.Remove(model);

                var tmp = Path.Combine(ModelsDir, model.FileName + ".tmp");
                if (File.Exists(tmp)) File.Delete(tmp);
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                model.IsDownloading            = false;
                model.DownloadProgress         = 0;
                model.DownloadSpeedBytesPerSec = 0;
                model.DownloadError            = ex.Message.Length > 120
                    ? ex.Message[..120] + "…"
                    : ex.Message;
                _activeDownloads.Remove(model);
            });
        }
    }

    [RelayCommand]
    private void CancelDownload(ModelItemViewModel model)
    {
        if (_activeDownloads.TryGetValue(model, out var cts))
            cts.Cancel();
    }

    [RelayCommand]
    private async Task SetActiveAsync(ModelItemViewModel model)
    {
        // Aktivní příznak resetujeme jen v rámci téže kategorie (jeden chat
        // model + jeden image model = dva nezávislé „aktivní" sloty).
        foreach (var section in RecommendedSections)
            foreach (var m in section.Models.Where(x => x.Category == model.Category))
                m.IsActive = false;

        foreach (var m in DownloadedModels.Where(x => x.Category == model.Category))
            m.IsActive = false;
        foreach (var m in SearchResults.Where(x => x.Category == model.Category))
            m.IsActive = false;

        model.IsActive = true;

        if (model.Category == ModelCategory.Chat)
        {
            _settings.Settings.DefaultChatModelName = model.Name;
            await _settings.SaveAsync();
        }
    }

    [RelayCommand]
    private void OpenModelsFolder()
    {
        Directory.CreateDirectory(ModelsDir);
        AIStudio.Infrastructure.Services.PlatformShell.Open(ModelsDir);
    }

    [RelayCommand]
    private void RescanModels()
    {
        ScanLocalModelsIntoDownloaded();
        // Po skenu zaktualizujeme i ikony „Staženo" v ostatních tabech.
        foreach (var section in RecommendedSections)
            MergeDownloadedFlagsInto(section.Models);
        MergeDownloadedFlagsInto(SearchResults);
    }

    [RelayCommand]
    private void OpenModelPage(ModelItemViewModel model)
    {
        if (string.IsNullOrEmpty(model.ModelPageUrl)) return;
        AIStudio.Infrastructure.Services.PlatformShell.OpenUrl(model.ModelPageUrl);
    }

    [RelayCommand]
    private async Task DeleteModelAsync(ModelItemViewModel model)
    {
        if (_activeDownloads.TryGetValue(model, out var cts))
            cts.Cancel();

        // Pokud je tento model právě v VRAM, odpoj ho (jinak by Delete selhal).
        var isLoaded = _llama.IsLoaded
                       && string.Equals(_llama.LoadedModelName, model.Name,
                                        StringComparison.OrdinalIgnoreCase);
        if (isLoaded)
        {
            try { await _llama.UnloadModelAsync(); }
            catch (Exception ex) { Log.Warning(ex, "DeleteModel: unload selhalo"); }
        }

        var path = Path.Combine(ModelsDir, model.FileName);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DeleteModel: nelze smazat {Path}", path);
            model.DownloadError = $"Nelze smazat: {ex.Message}";
            return;
        }

        if (string.Equals(_settings.Settings.DefaultChatModelName, model.Name,
                          StringComparison.OrdinalIgnoreCase))
        {
            _settings.Settings.DefaultChatModelName = string.Empty;
            try { await _settings.SaveAsync(); } catch { /* best effort */ }
        }

        model.IsDownloaded       = false;
        model.IsActive           = false;
        model.IsConfirmingDelete = false;
        model.SizeOnDisk         = string.Empty;
        DownloadedModels.Remove(model);

        _settings.NotifyModelLibraryChanged();
    }

    [RelayCommand]
    private async Task AddLocalModelAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;

        var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Přidat lokální model",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Model (.gguf, .safetensors, .ckpt, .pt)")
                    { Patterns = ["*.gguf", "*.safetensors", "*.ckpt", "*.pt", "*.bin"] },
                new FilePickerFileType("Všechny soubory") { Patterns = ["*"] },
            ],
        });

        if (files.Count == 0) return;

        var sourcePath = files[0].Path.LocalPath;
        var fileName   = Path.GetFileName(sourcePath);
        var destPath   = Path.Combine(ModelsDir, fileName);

        if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
        {
            RescanModels();
            return;
        }

        var modelName  = Path.GetFileNameWithoutExtension(fileName);
        var localModel = new ModelItemViewModel
        {
            Name             = modelName,
            Description      = $"Lokální model — {sourcePath}",
            FileName         = fileName,
            Category         = GuessCategoryByExt(fileName),
            Source           = ModelSource.Local,
            IsDownloading    = true,
            DownloadProgress = 0,
        };

        DownloadedModels.Insert(0, localModel);
        SelectedModel = localModel;

        var cts = new CancellationTokenSource();
        _activeDownloads[localModel] = cts;

        try
        {
            Directory.CreateDirectory(ModelsDir);
            await CopyWithProgressAsync(sourcePath, destPath, localModel, cts.Token);

            Dispatcher.UIThread.Post(() =>
            {
                localModel.IsDownloading    = false;
                localModel.IsDownloaded     = true;
                localModel.DownloadProgress = 100;
                localModel.SizeOnDisk       = FormatFileSize(new FileInfo(destPath).Length);
                _activeDownloads.Remove(localModel);

                _settings.NotifyModelLibraryChanged();
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                DownloadedModels.Remove(localModel);
                _activeDownloads.Remove(localModel);
                var tmp = destPath + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                localModel.IsDownloading = false;
                localModel.DownloadError = ex.Message.Length > 120 ? ex.Message[..120] + "…" : ex.Message;
                _activeDownloads.Remove(localModel);
            });
        }
    }

    private static ModelCategory GuessCategoryByExt(string fileName)
    {
        // GGUF = chat (LlamaSharp); .safetensors / .ckpt / .pt = obvykle image
        // checkpointy. Není 100%, ale heuristika stačí — uživatel může přepnout
        // ručně, nebo to discovery najde ve vlastním tabu.
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext == ".gguf" ? ModelCategory.Chat : ModelCategory.Image;
    }

    private static async Task CopyWithProgressAsync(
        string             sourcePath,
        string             destPath,
        ModelItemViewModel model,
        CancellationToken  ct)
    {
        const int bufSize = 81_920;
        var total  = new FileInfo(sourcePath).Length;
        var buffer = new byte[bufSize];
        var copied = 0L;

        var sw             = Stopwatch.StartNew();
        var lastSpeedTime  = sw.Elapsed;
        var lastSpeedBytes = 0L;
        var speedBps       = 0.0;

        await using var src = File.OpenRead(sourcePath);
        await using var dst = new FileStream(destPath + ".tmp",
            FileMode.Create, FileAccess.Write, FileShare.None, bufSize, useAsync: true);

        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;

            var now       = sw.Elapsed;
            var sinceLast = now - lastSpeedTime;
            if (sinceLast.TotalSeconds >= 0.5)
            {
                speedBps       = (copied - lastSpeedBytes) / sinceLast.TotalSeconds;
                lastSpeedTime  = now;
                lastSpeedBytes = copied;
            }

            var pct    = (double)copied / total * 100;
            var bCopy  = copied;
            var bSpeed = speedBps;
            Dispatcher.UIThread.Post(() =>
            {
                model.DownloadedBytes          = bCopy;
                model.TotalBytes               = total;
                model.DownloadProgress         = pct;
                model.DownloadSpeedBytesPerSec = bSpeed;
            });
        }

        await dst.FlushAsync(ct);
        dst.Close();

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(destPath + ".tmp", destPath);
    }

    // ── Tab 2: Stažené (lokální sken) ─────────────────────────────────────────

    /// <summary>
    /// Naskenuje <see cref="ModelsDir"/> a naplní <see cref="DownloadedModels"/>
    /// modely, které tam reálně jsou. Pokrývá všechny rozumné formáty
    /// (.gguf chat, .safetensors / .ckpt / .pt / .bin image).
    ///
    /// I/O část (enumerate files + FileInfo lookup) běží volajícím threadem;
    /// finální mutace ObservableCollection se přesouvá na UI thread, takže
    /// metoda je bezpečná i z background tasku.
    /// </summary>
    private void ScanLocalModelsIntoDownloaded()
    {
        if (!Directory.Exists(ModelsDir))
        {
            Dispatcher.UIThread.Post(() => DownloadedModels.Clear());
            return;
        }

        var defaultModel = _settings.Settings.DefaultChatModelName;
        var patterns = new[] { "*.gguf", "*.safetensors", "*.ckpt", "*.pt", "*.bin" };

        // Nejdřív si všechno posbíráme do lokálního listu (běží na pozadí),
        // pak jediným Dispatch postem nahrnem do kolekce — méně jitteru v UI
        // než N samostatných Add-ů.
        var collected = new List<ModelItemViewModel>();

        try
        {
            foreach (var pattern in patterns)
            {
                foreach (var path in Directory.EnumerateFiles(ModelsDir, pattern, SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(path);
                    var name     = Path.GetFileNameWithoutExtension(fileName);
                    var info     = new FileInfo(path);

                    var item = new ModelItemViewModel
                    {
                        Name           = name,
                        Description    = $"Lokální soubor: {Path.GetRelativePath(ModelsDir, path)}",
                        FileName       = Path.GetRelativePath(ModelsDir, path).Replace('\\', '/'),
                        Category       = GuessCategoryByExt(fileName),
                        Source         = ModelSource.Local,
                        Quantization   = ExtractQuantizationFromName(fileName),
                        Size           = FormatFileSize(info.Length),
                        SizeOnDisk     = FormatFileSize(info.Length),
                        IsDownloaded   = true,
                        VramRequiredGb = (int)Math.Ceiling(info.Length / 1_073_741_824.0 * 1.3),
                    };

                    if (!string.IsNullOrEmpty(defaultModel) &&
                        string.Equals(item.Name, defaultModel, StringComparison.OrdinalIgnoreCase))
                    {
                        item.IsActive = true;
                    }

                    collected.Add(item);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ModelManager: sken {Dir} selhal", ModelsDir);
        }

        Dispatcher.UIThread.Post(() =>
        {
            DownloadedModels.Clear();
            foreach (var item in collected)
                DownloadedModels.Add(item);
        });
    }

    private void RefreshDownloadedTab() => ScanLocalModelsIntoDownloaded();

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="DiscoveredModel"/> → UI item. Categorii odhadne z PickKind,
    /// zachovává <see cref="DiscoveredModel.ProviderRef"/> pro pozdější HF file
    /// picker.
    /// </summary>
    private ModelItemViewModel BuildItemFromDiscovered(DiscoveredModel d, PickKind kind)
    {
        var category = kind switch
        {
            PickKind.Chat  => ModelCategory.Chat,
            PickKind.Image => ModelCategory.Image,
            PickKind.Lora  => ModelCategory.Image,         // LoRA dává smysl jen pro image
            _              => ModelCategory.Chat
        };

        var source = d.Provider switch
        {
            "HuggingFace" => ModelSource.HuggingFace,
            "Civitai"     => ModelSource.Civitai,
            _             => ModelSource.Local
        };

        return new ModelItemViewModel
        {
            Name           = d.Name,
            Description    = string.IsNullOrEmpty(d.Description)
                              ? (string.IsNullOrEmpty(d.Author) ? "" : $"od {d.Author}")
                              : d.Description,
            FileName       = d.FileName,
            DownloadUrl    = d.DownloadUrl,
            ModelPageUrl   = d.ModelPageUrl,
            ProviderRef    = d.ProviderRef,
            Source         = source,
            Category       = category,
            Quantization   = d.FileFormat ?? string.Empty,
            Size           = d.SizeBytes > 0 ? FormatFileSize(d.SizeBytes) : "",
            VramRequiredGb = d.SizeBytes > 0 ? (int)Math.Ceiling(d.SizeBytes / 1_073_741_824.0 * 1.3) : 0,
            ThumbnailUrl   = d.ThumbnailUrl ?? string.Empty,
            BaseModel      = d.BaseModel ?? string.Empty,
            IsNsfw         = d.Nsfw,
            DownloadCount  = d.Downloads,
            Rating         = d.Rating,
        };
    }

    /// <summary>
    /// Pro každou položku v kolekci zkontroluje, jestli na disku existuje
    /// soubor se stejným jménem; pokud ano, nastaví IsDownloaded=true (badge
    /// „Staženo"). Volá se po skenu lokálního adresáře.
    /// </summary>
    private void MergeDownloadedFlagsInto(IEnumerable<ModelItemViewModel> items)
    {
        if (!Directory.Exists(ModelsDir)) return;

        // Rychlý lookup: jméno souboru (lower) → existuje na disku
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(ModelsDir, "*", SearchOption.AllDirectories))
                existing.Add(Path.GetFileName(path));
        }
        catch { /* best effort */ }

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.FileName)) continue;
            var bareName = Path.GetFileName(item.FileName);
            if (existing.Contains(bareName))
                item.IsDownloaded = true;
        }
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1_024         => $"{bytes} B",
        < 1_048_576     => $"{bytes / 1_024.0:F0} KB",
        < 1_073_741_824 => $"{bytes / 1_048_576.0:F1} MB",
        _               => $"{bytes / 1_073_741_824.0:F2} GB"
    };
}
