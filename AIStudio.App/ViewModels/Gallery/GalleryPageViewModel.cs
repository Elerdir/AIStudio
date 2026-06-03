using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Enums;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.App.ViewModels.ImageStudio;
using AIStudio.Infrastructure.Services;

namespace AIStudio.App.ViewModels.Gallery;

/// <summary>
/// Galerie — samostatná záložka se všemi vygenerovanými obrázky (z SQLite, tj. ze
/// sdílené výstupní složky). Master-detail: mřížka náhledů + detail s metadaty
/// (model, prompt, seed, rozměry, datum) a akcemi (upscale, editace, …).
///
/// <para>Skládá existující stavební kameny: <see cref="IImageRepository"/> (data +
/// metadata, stránkovaně), <see cref="GeneratedImageViewModel"/> (položka), a
/// <see cref="IChatImageOrchestrator"/> pro upscale (<c>UpscaleImageAsync</c>) i
/// inline Kontext editaci (<c>GenerateAsync</c> s referencí). „Upravit v Image
/// Studiu" deleguje na <see cref="ImageStudioPageViewModel.OpenImageForEditing"/>.</para>
///
/// <para>Videa zatím nejsou (video gen v appce není) — až přijdou, přidá se typ
/// média + filtr; struktura stránky to umožní.</para>
/// </summary>
public partial class GalleryPageViewModel : ViewModelBase
{
    private readonly IImageRepository         _imageRepo;
    private readonly IChatImageOrchestrator?  _orch;
    private readonly INavigationService       _nav;
    private readonly ImageStudioPageViewModel _imageStudio;
    private readonly IDialogService?          _dialog;

    private const int PageSize = 50;
    private CancellationTokenSource? _busyCts;

    private readonly Video.VideoPageViewModel? _video;

    public GalleryPageViewModel(
        IImageRepository          imageRepo,
        INavigationService        nav,
        ImageStudioPageViewModel  imageStudio,
        IChatImageOrchestrator?   orch   = null,
        IDialogService?           dialog = null,
        Video.VideoPageViewModel? video  = null)
    {
        _imageRepo   = imageRepo;
        _nav         = nav;
        _imageStudio = imageStudio;
        _orch        = orch;
        _dialog      = dialog;
        _video       = video;

        Images.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasImages));
            OnPropertyChanged(nameof(CanLoadMore));
            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(ShowLoadingState));
        };
    }

    // ── Data ──────────────────────────────────────────────────────────────────

    public ObservableCollection<GeneratedImageViewModel> Images { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore), nameof(StatusLine))]
    private int _totalInDb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(ShowLoadingState))]
    private bool _isLoading;

    [ObservableProperty] private bool _isLoadingMore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanAnimateSelected))]
    private GeneratedImageViewModel? _selectedImage;

    public bool HasImages    => Images.Count > 0;
    public bool HasSelection => SelectedImage is not null;
    public bool CanLoadMore  => Images.Count < TotalInDb;

    /// <summary>Spinner pro první načtení (žádné položky a probíhá load).</summary>
    public bool ShowLoadingState => IsLoading && !HasImages;
    /// <summary>Prázdný stav — jen když nic neběží (ať neprobliká během načítání).</summary>
    public bool ShowEmptyState   => !IsLoading && !HasImages;

    public string StatusLine => TotalInDb switch
    {
        0 when MediaFilterIndex == 2 => "Zatím žádná videa",
        0 when MediaFilterIndex == 1 => "Zatím žádné obrázky",
        0 when HasActiveFilter       => "Filtru nic neodpovídá",
        0                            => "Žádné vygenerované obrázky",
        var t when Images.Count >= t => $"Zobrazeno všech {t}",
        var t => $"Zobrazeno {Images.Count} z {t}",
    };

    // ── Rozdělení podle typu média (Vše / Obrázky / Videa) ─────────────────────

    /// <summary>0 = Vše, 1 = Obrázky, 2 = Videa. Filtruje přes celou DB (MediaType).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMediaAll), nameof(IsMediaImages), nameof(IsMediaVideos),
                              nameof(StatusLine))]
    private int _mediaFilterIndex;

    public bool IsMediaAll    => MediaFilterIndex == 0;
    public bool IsMediaImages => MediaFilterIndex == 1;
    public bool IsMediaVideos => MediaFilterIndex == 2;

    [RelayCommand]
    private void SelectMediaFilter(string index)
    {
        MediaFilterIndex = int.TryParse(index, out var i) ? i : 0;
    }

    partial void OnMediaFilterIndexChanged(int value) => _ = RefreshAsync();

    // ── Filtr ─────────────────────────────────────────────────────────────────

    /// <summary>Fulltext hledaný v promptu i názvu modelu (debounced reload).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilter))]
    private string _searchText = string.Empty;

    /// <summary>Nabídka modelů pro filtr (první položka = „Všechny modely").</summary>
    public ObservableCollection<ModelFilterOption> ModelOptions { get; } = new()
    {
        ModelFilterOption.All,
    };

    /// <summary>Vybraný model ve filtru. <see cref="ModelFilterOption.All"/> = bez omezení.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilter))]
    private ModelFilterOption _selectedModelOption = ModelFilterOption.All;

    public bool HasActiveFilter =>
        !string.IsNullOrWhiteSpace(SearchText) || SelectedModelOption.Value is not null;

    private CancellationTokenSource? _searchDebounceCts;

    private ImageQueryFilter BuildFilter() => new(
        Search:    string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
        ModelName: SelectedModelOption.Value,
        MediaType: MediaFilterIndex switch
        {
            1 => MediaTypes.Image,
            2 => MediaTypes.Video,
            _ => null,
        });

    partial void OnSearchTextChanged(string value) => DebouncedReload();

    partial void OnSelectedModelOptionChanged(ModelFilterOption value) => _ = RefreshAsync();

    /// <summary>Drobné zpoždění (250 ms) — ať se nereloaduje při každém stisku klávesy.</summary>
    private void DebouncedReload()
    {
        _searchDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(250, cts.Token); }
            catch (OperationCanceledException) { return; }
            if (!cts.IsCancellationRequested)
                await Dispatcher.UIThread.InvokeAsync(RefreshAsync);
        });
    }

    /// <summary>Vyčistí všechny filtry a načte znovu.</summary>
    [RelayCommand]
    private async Task ClearFilter()
    {
        _searchDebounceCts?.Cancel();
        var changed = false;
        if (!string.IsNullOrEmpty(SearchText)) { SearchText = string.Empty; changed = true; }
        if (SelectedModelOption.Value is not null)
        {
            // setter spustí OnSelectedModelOptionChanged → RefreshAsync, takže netřeba dvojí reload
            SelectedModelOption = ModelFilterOption.All;
            return;
        }
        if (changed) await RefreshAsync();
    }

    // ── Busy stav (upscale / editace) ─────────────────────────────────────────

    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private string _busyStatus = string.Empty;
    [ObservableProperty] private int    _busyProgress;

    /// <summary>Inline instrukce pro Kontext editaci vybraného obrázku.</summary>
    [ObservableProperty] private string _editInstruction = string.Empty;

    /// <summary>True když je editace/upscale k dispozici (orchestrátor injektován).</summary>
    public bool ActionsSupported => _orch is not null;

    // ── Načítání ──────────────────────────────────────────────────────────────

    public override Task InitializeAsync() => RefreshAsync();

    /// <summary>Command wrapper pro tlačítko „Obnovit" v hlavičce.</summary>
    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    /// <summary>Načte první stránku obrázků (volá se při otevření záložky / změně filtru).</summary>
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var filter  = BuildFilter();
            var total   = await _imageRepo.CountImagesAsync(filter);
            var records = await _imageRepo.LoadImagesPagedAsync(skip: 0, take: PageSize, filter);
            var models  = await _imageRepo.GetDistinctModelsAsync();
            // Stavba VM + File.Exists mimo UI vlákno (disk I/O) — náhledy se navíc
            // dekódují asynchronně (viz GeneratedImageViewModel), takže UI se nesekne.
            var items   = await Task.Run(() => BuildItems(records));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SyncModelOptions(models);
                Images.Clear();
                foreach (var it in items) Images.Add(it);
                TotalInDb = total;
                SelectedImage = Images.Count > 0 ? Images[0] : null;
                OnPropertyChanged(nameof(CanLoadMore));
                OnPropertyChanged(nameof(StatusLine));
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Galerie: načtení obrázků selhalo");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsLoadingMore || !CanLoadMore) return;
        IsLoadingMore = true;
        try
        {
            var skip    = Images.Count;
            var records = await _imageRepo.LoadImagesPagedAsync(skip, PageSize, BuildFilter());
            var items   = await Task.Run(() => BuildItems(records));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var it in items) Images.Add(it);
                OnPropertyChanged(nameof(CanLoadMore));
                OnPropertyChanged(nameof(StatusLine));
            });
        }
        catch (Exception ex) { Log.Warning(ex, "Galerie: LoadMore selhal"); }
        finally { IsLoadingMore = false; }
    }

    /// <summary>
    /// Aktualizuje nabídku modelů ve filtru tak, aby odpovídala DB, a přitom zachová
    /// uživatelův výběr (pokud daný model stále existuje). Volá se na UI vlákně.
    /// </summary>
    private void SyncModelOptions(IReadOnlyList<string> models)
    {
        var desired = new List<ModelFilterOption> { ModelFilterOption.All };
        desired.AddRange(models.Select(ModelFilterOption.For));

        // Když je seznam stejný, neměň (ať neskáče výběr/UI).
        if (desired.Count == ModelOptions.Count &&
            desired.Zip(ModelOptions, (a, b) => a.Value == b.Value).All(eq => eq))
            return;

        var previouslySelected = SelectedModelOption.Value;
        ModelOptions.Clear();
        foreach (var opt in desired) ModelOptions.Add(opt);

        // Obnov výběr nebo spadni na „Všechny modely". Používáme SetProperty (ne generovaný
        // setter), aby se NEspustil OnSelectedModelOptionChanged → další RefreshAsync (rekurze).
        var restored = ModelOptions.FirstOrDefault(o => o.Value == previouslySelected) ?? ModelFilterOption.All;
        if (!ReferenceEquals(restored, SelectedModelOption))
        {
            // Záměrně přes backing field — generovaný setter by spustil reload (rekurze).
#pragma warning disable MVVMTK0034
            SetProperty(ref _selectedModelOption, restored, nameof(SelectedModelOption));
#pragma warning restore MVVMTK0034
            OnPropertyChanged(nameof(HasActiveFilter));
        }
    }

    /// <summary>
    /// Mapuje <see cref="ImageRecord"/> → VM, přeskakuje smazané soubory. Čistá funkce
    /// (žádný UI stav) — volá se na pozadí přes <c>Task.Run</c>, ať <c>File.Exists</c>
    /// neblokuje UI vlákno.
    /// </summary>
    private static List<GeneratedImageViewModel> BuildItems(IReadOnlyList<ImageRecord> records)
    {
        var list = new List<GeneratedImageViewModel>(records.Count);
        foreach (var rec in records)
        {
            if (!File.Exists(rec.FilePath)) continue;
            list.Add(new GeneratedImageViewModel
            {
                Id        = rec.Id,
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
                MediaType = rec.MediaType,
            });
        }
        return list;
    }

    // ── Akce ──────────────────────────────────────────────────────────────────

    /// <summary>Náhled na plnou velikost v samostatném okně.</summary>
    [RelayCommand]
    private void OpenPreview()
    {
        if (SelectedImage is { FilePath: { } p } && File.Exists(p))
            _dialog?.ShowImagePreview(p);
    }

    /// <summary>Otevře složku s obrázkem v systémovém prohlížeči souborů.</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        var path = SelectedImage?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            try { PlatformShell.Open(dir); }
            catch (Exception ex) { Log.Warning(ex, "Galerie: OpenFolder selhal"); }
        }
    }

    /// <summary>Uloží kopii obrázku na zvolené místo.</summary>
    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (_dialog is null || SelectedImage is not { FilePath: { } src } || !File.Exists(src)) return;
        var ext  = Path.GetExtension(src).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) ext = "png";
        var dest = await _dialog.SaveFileAsync(
            "Uložit obrázek jako…",
            Path.GetFileNameWithoutExtension(src),
            ext,
            new[] { new FileFilter($"Obrázek ({ext.ToUpperInvariant()})", new[] { $"*.{ext}" }) });
        if (string.IsNullOrEmpty(dest)) return;
        try
        {
            await using var input  = File.OpenRead(src);
            await using var output = File.Create(dest);
            await input.CopyToAsync(output);
        }
        catch (Exception ex) { Log.Warning(ex, "Galerie: SaveAs selhal"); }
    }

    /// <summary>Smaže obrázek z galerie (DB i soubor na disku).</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        var img = SelectedImage;
        if (img is null) return;
        try
        {
            await _imageRepo.DeleteImageAsync(img.Id);
            try { if (File.Exists(img.FilePath)) File.Delete(img.FilePath); }
            catch (Exception ex) { Log.Warning(ex, "Galerie: smazání souboru selhalo"); }

            Dispatcher.UIThread.Post(() =>
            {
                var idx = Images.IndexOf(img);
                Images.Remove(img);
                TotalInDb = Math.Max(0, TotalInDb - 1);
                SelectedImage = Images.Count > 0 ? Images[Math.Min(idx, Images.Count - 1)] : null;
            });
        }
        catch (Exception ex) { Log.Warning(ex, "Galerie: DeleteImageAsync selhal"); }
    }

    /// <summary>Otevře obrázek k editaci v Image Studiu (reference + přepne tab).</summary>
    [RelayCommand]
    private void EditInStudio()
    {
        var path = SelectedImage?.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        _imageStudio.OpenImageForEditing(path);
        _nav.Navigate(NavigationPage.ImageStudio);
    }

    /// <summary>True když lze vybraný obrázek poslat do Videa (je to obrázek, ne video).</summary>
    public bool CanAnimateSelected => _video is not null && SelectedImage is { IsVideo: false };

    /// <summary>Pošle vybraný obrázek do Video tabu jako vstup pro obrázek→video.</summary>
    [RelayCommand]
    private void AnimateInVideo()
    {
        var path = SelectedImage?.FilePath;
        if (_video is null || string.IsNullOrEmpty(path) || !File.Exists(path)
            || SelectedImage is { IsVideo: true }) return;
        _video.StartFromImage(path);
        _nav.Navigate(NavigationPage.Video);
    }

    /// <summary>Upscale vybraného obrázku (ESRGAN ~2×). Po dokončení reload galerie.</summary>
    [RelayCommand]
    private async Task UpscaleSelectedAsync()
    {
        var path = SelectedImage?.FilePath;
        if (_orch is null || string.IsNullOrEmpty(path) || !File.Exists(path) || IsBusy) return;

        await RunBusyAsync("Upscaluji…", (progress, ct) =>
            _orch.UpscaleImageAsync(path, progress, ct));
    }

    /// <summary>Inline editace vybraného obrázku dle instrukce (FLUX Kontext).</summary>
    [RelayCommand]
    private async Task ApplyInlineEditAsync()
    {
        var path        = SelectedImage?.FilePath;
        var instruction = EditInstruction.Trim();
        if (_orch is null || string.IsNullOrEmpty(path) || !File.Exists(path)
            || string.IsNullOrWhiteSpace(instruction) || IsBusy) return;

        var ok = await RunBusyAsync("Upravuji…", (progress, ct) =>
            _orch.GenerateAsync(instruction, path, progress, ct));
        if (ok) EditInstruction = string.Empty;
    }

    /// <summary>Zruší probíhající upscale/editaci.</summary>
    [RelayCommand]
    private void CancelBusy() => _busyCts?.Cancel();

    /// <summary>
    /// Společný běh dlouhé akce (upscale/edit) s progress + statusem + reloadem
    /// galerie po úspěchu. Vrací true při úspěchu.
    /// </summary>
    private async Task<bool> RunBusyAsync(
        string status,
        Func<IProgress<int>, CancellationToken, Task<ChatImageGenerationResult>> action)
    {
        using var cts = new CancellationTokenSource();
        _busyCts      = cts;
        IsBusy        = true;
        BusyProgress  = 0;
        BusyStatus    = status;

        var progress = new Progress<int>(p => Dispatcher.UIThread.Post(() => BusyProgress = p));
        try
        {
            var result = await action(progress, cts.Token);
            if (!result.Success)
            {
                BusyStatus = result.ErrorMessage ?? "Operace selhala";
                await Task.Delay(2500, CancellationToken.None);
                return false;
            }
            await RefreshAsync();   // nový obrázek se objeví nahoře
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Galerie: akce '{Status}' selhala", status);
            return false;
        }
        finally
        {
            IsBusy     = false;
            BusyStatus = string.Empty;
            _busyCts   = null;
        }
    }
}
