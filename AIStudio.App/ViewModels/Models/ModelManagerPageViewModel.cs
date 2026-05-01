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

public partial class ModelManagerPageViewModel : ViewModelBase
{
    private readonly IDownloadService    _downloader;
    private readonly ISettingsService    _settings;
    private readonly ILlamaService       _llama;
    private readonly IHuggingFaceClient  _hf;

    // ── Online search (HuggingFace) ───────────────────────────────────────────
    [ObservableProperty] private string  _hfSearchQuery       = string.Empty;
    [ObservableProperty] private bool    _isSearchingHf;
    [ObservableProperty] private string  _hfSearchError       = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHfFiles))]
    private HfModelInfoVm? _selectedHfModel;
    [ObservableProperty] private bool    _isLoadingHfFiles;

    public ObservableCollection<HfModelInfoVm> HfSearchResults { get; } = new();
    public ObservableCollection<HfFileInfoVm>  HfFiles         { get; } = new();
    public bool HasHfFiles => HfFiles.Count > 0;

    [ObservableProperty] private ModelItemViewModel? _selectedModel;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _selectedTabIndex;

    private readonly List<ModelItemViewModel> _allModels;

    // Aktivní stahování — klíč: model, hodnota: CTS pro zrušení
    private readonly Dictionary<ModelItemViewModel, CancellationTokenSource> _activeDownloads = new();

    public ObservableCollection<ModelItemViewModel> DisplayedModels { get; } = new();

    // Cesta ke složce s modely — čte custom cestu z nastavení, fallback na výchozí
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

    public ModelManagerPageViewModel(IDownloadService    downloader,
                                     ISettingsService    settings,
                                     ILlamaService       llama,
                                     IHuggingFaceClient  hf)
    {
        _downloader = downloader;
        _settings   = settings;
        _llama      = llama;
        _hf         = hf;

        _allModels = BuildModelCatalog();
        ScanExistingDownloads();
        RefreshDisplayed();
    }

    // ── Filtering ──────────────────────────────────────────────────────────────
    partial void OnSearchTextChanged(string value) => RefreshDisplayed();
    partial void OnSelectedTabIndexChanged(int value) => RefreshDisplayed();

    private void RefreshDisplayed()
    {
        DisplayedModels.Clear();
        var tab    = SelectedTabIndex;
        var search = SearchText.Trim().ToLowerInvariant();

        foreach (var m in _allModels)
        {
            bool categoryMatch = tab switch
            {
                1 => m.Category == ModelCategory.Chat,
                2 => m.Category == ModelCategory.Image,
                3 => m.IsDownloaded,                    // tab „Staženo"
                _ => true
            };
            bool searchMatch = string.IsNullOrEmpty(search)
                || m.Name.ToLowerInvariant().Contains(search)
                || m.Description.ToLowerInvariant().Contains(search);

            if (categoryMatch && searchMatch)
                DisplayedModels.Add(m);
        }
    }

    // ── Commands ───────────────────────────────────────────────────────────────
    [RelayCommand]
    private void SelectTab(string index)
    {
        if (int.TryParse(index, out var i))
            SelectedTabIndex = i;
    }

    [RelayCommand]
    private void SelectModel(ModelItemViewModel model)
    {
        if (SelectedModel is not null)
            SelectedModel.IsSelected = false;
        model.IsSelected = true;
        SelectedModel = model;
    }

    [RelayCommand]
    private async Task SetActiveAsync(ModelItemViewModel model)
    {
        foreach (var m in _allModels.Where(x => x.Category == model.Category))
            m.IsActive = false;
        model.IsActive = true;

        // Uložíme výchozí chat model do nastavení
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
        Process.Start("explorer.exe", ModelsDir);
    }

    [RelayCommand]
    private void RescanModels()
    {
        ScanExistingDownloads();
        RefreshDisplayed();
    }

    [RelayCommand]
    private async Task AddLocalModelAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;

        var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Přidat lokální GGUF model",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("GGUF model") { Patterns = ["*.gguf"] },
                new FilePickerFileType("Všechny soubory") { Patterns = ["*"] },
            ],
        });

        if (files.Count == 0) return;

        var sourcePath = files[0].Path.LocalPath;
        var fileName   = Path.GetFileName(sourcePath);
        var destPath   = Path.Combine(ModelsDir, fileName);

        // Pokud je soubor již ve složce modelů, jen přeskenujeme
        if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
        {
            RescanModels();
            return;
        }

        // Soubor je jinde — přidáme ho do seznamu a zkopírujeme s průběhem
        var modelName  = Path.GetFileNameWithoutExtension(fileName);
        var localModel = new ModelItemViewModel
        {
            Name         = modelName,
            Description  = $"Lokální model — {sourcePath}",
            FileName     = fileName,
            Category     = ModelCategory.Chat,
            Source       = ModelSource.Local,
            IsDownloading = true,
            DownloadProgress = 0,
        };

        _allModels.Insert(0, localModel);
        RefreshDisplayed();
        SelectedModel = localModel;

        var cts = new CancellationTokenSource();
        _activeDownloads[localModel] = cts;

        try
        {
            Directory.CreateDirectory(ModelsDir);
            await CopyWithProgressAsync(sourcePath, destPath, localModel, cts.Token);

            Dispatcher.UIThread.Post(() =>
            {
                localModel.IsDownloading  = false;
                localModel.IsDownloaded   = true;
                localModel.DownloadProgress = 100;
                localModel.SizeOnDisk     = FormatFileSize(new FileInfo(destPath).Length);
                _activeDownloads.Remove(localModel);

                _settings.NotifyModelLibraryChanged();
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _allModels.Remove(localModel);
                _activeDownloads.Remove(localModel);
                var tmp = destPath + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
                RefreshDisplayed();
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                localModel.IsDownloading  = false;
                localModel.DownloadError  = ex.Message.Length > 120 ? ex.Message[..120] + "…" : ex.Message;
                _activeDownloads.Remove(localModel);
            });
        }
    }

    private static async Task CopyWithProgressAsync(
        string            sourcePath,
        string            destPath,
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

    [RelayCommand]
    private async Task DownloadAsync(ModelItemViewModel model)
    {
        if (model.IsDownloading) return;
        if (string.IsNullOrEmpty(model.DownloadUrl))
        {
            model.DownloadError = "URL pro stažení není k dispozici.";
            return;
        }

        // Zvol správný token dle zdroje
        var token = model.Source == ModelSource.Civitai
            ? _settings.Settings.CivitaiApiKey
            : _settings.Settings.HuggingFaceToken;

        // Varování: Civitai vyžaduje API klíč
        if (model.Source == ModelSource.Civitai && string.IsNullOrWhiteSpace(token))
        {
            model.DownloadError =
                "Civitai vyžaduje API klíč. Nastav ho v Nastavení → Stahování, " +
                "poté zkus znovu.";
            return;
        }

        var destPath = Path.Combine(ModelsDir, model.FileName);
        var cts = new CancellationTokenSource();
        _activeDownloads[model] = cts;

        // Reset stavu
        model.DownloadError     = string.Empty;
        model.IsDownloading     = true;
        model.DownloadProgress  = 0;
        model.DownloadedBytes   = 0;
        model.TotalBytes        = 0;

        var progress = new Progress<DownloadProgressInfo>(info =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                model.DownloadedBytes          = info.Downloaded;
                model.TotalBytes               = info.Total;
                model.DownloadProgress         = info.Percent;
                model.DownloadSpeedBytesPerSec = info.BytesPerSecond;
            });
        });

        try
        {
            await _downloader.DownloadFileAsync(model.DownloadUrl, destPath, progress, token, cts.Token);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                model.IsDownloaded            = true;
                model.IsDownloading           = false;
                model.DownloadProgress        = 100;
                model.DownloadSpeedBytesPerSec = 0;
                _activeDownloads.Remove(model);

                // Pickery v Chat / Image Studio se musí o nový model dozvědět
                _settings.NotifyModelLibraryChanged();
            });
        }
        catch (OperationCanceledException)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                model.IsDownloading           = false;
                model.DownloadProgress        = 0;
                model.DownloadedBytes         = 0;
                model.TotalBytes              = 0;
                model.DownloadSpeedBytesPerSec = 0;
                _activeDownloads.Remove(model);

                // Smaž nedokončený dočasný soubor
                var tmp = Path.Combine(ModelsDir, model.FileName + ".tmp");
                if (File.Exists(tmp)) File.Delete(tmp);
            });
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                model.IsDownloading           = false;
                model.DownloadProgress        = 0;
                model.DownloadSpeedBytesPerSec = 0;
                model.DownloadError           = ex.Message.Length > 120
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
    private void OpenModelPage(ModelItemViewModel model)
    {
        if (string.IsNullOrEmpty(model.ModelPageUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(model.ModelPageUrl) { UseShellExecute = true });
        }
        catch { /* Ignorujeme — prohlížeč nemusí být k dispozici */ }
    }

    // ── HuggingFace online search ────────────────────────────────────────────

    [RelayCommand]
    private async Task SearchHuggingFaceAsync()
    {
        if (IsSearchingHf || string.IsNullOrWhiteSpace(HfSearchQuery)) return;

        IsSearchingHf  = true;
        HfSearchError  = string.Empty;
        HfSearchResults.Clear();
        HfFiles.Clear();
        SelectedHfModel = null;

        try
        {
            var results = await _hf.SearchGgufModelsAsync(HfSearchQuery.Trim(), limit: 30);

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var r in results)
                    HfSearchResults.Add(new HfModelInfoVm(r));
            });
        }
        catch (Exception ex)
        {
            HfSearchError = $"Vyhledávání selhalo: {ex.Message}";
        }
        finally
        {
            IsSearchingHf = false;
        }
    }

    [RelayCommand]
    private async Task SelectHfModelAsync(HfModelInfoVm model)
    {
        if (model is null) return;

        SelectedHfModel = model;
        HfFiles.Clear();
        IsLoadingHfFiles = true;

        try
        {
            var files = await _hf.ListGgufFilesAsync(model.RepoId);
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var f in files)
                    HfFiles.Add(new HfFileInfoVm(f, model.RepoId));
                OnPropertyChanged(nameof(HasHfFiles));
            });
        }
        catch (Exception ex)
        {
            HfSearchError = $"Načtení souborů selhalo: {ex.Message}";
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

        // Sestavíme katalogovou položku z HF metadat — recyklujeme stávající
        // download flow (DownloadAsync), takže UX je identické s katalogem:
        // progress, rychlost, ETA, možnost zrušit a ulož do Models adresáře.
        var displayName = file.RepoId.Contains('/') ? file.RepoId.Split('/')[1] : file.RepoId;

        var item = new ModelItemViewModel
        {
            Name           = $"{displayName} · {Path.GetFileNameWithoutExtension(file.Path)}",
            Description    = $"Staženo z HuggingFace: {file.RepoId}",
            FileName       = file.Path,
            Category       = ModelCategory.Chat,           // heuristika — uživatel může později změnit
            Source         = ModelSource.HuggingFace,
            Quantization   = ExtractQuantizationFromName(file.Path),
            DownloadUrl    = _hf.BuildDownloadUrl(file.RepoId, file.Path),
            ModelPageUrl   = _hf.BuildModelPageUrl(file.RepoId),
            Size           = FormatFileSize(file.SizeBytes),
            VramRequiredGb = (int)Math.Ceiling(file.SizeBytes / 1_073_741_824.0 * 1.3),
        };

        // Přidáme ho do katalogu (na začátek, ať ho uživatel vidí)
        _allModels.Insert(0, item);
        RefreshDisplayed();
        SelectedModel = item;

        // Stáhneme stejnou cestou jako katalogové modely
        file.IsDownloading = true;
        try
        {
            await DownloadAsync(item);
            file.IsDownloaded = item.IsDownloaded;
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
        // Najdi posledních pár znaků za poslední pomlčkou — typicky "Q4_K_M", "Q5_K_S", "Q8_0", "F16"
        var dash = noExt.LastIndexOf('-');
        if (dash >= 0 && dash < noExt.Length - 1)
        {
            var tail = noExt[(dash + 1)..];
            if (tail.StartsWith('Q') || tail.StartsWith('F') || tail.StartsWith('B'))
                return tail.ToUpperInvariant();
        }
        return string.Empty;
    }

    [RelayCommand]
    private async Task DeleteModelAsync(ModelItemViewModel model)
    {
        // Zruš případné probíhající stahování
        if (_activeDownloads.TryGetValue(model, out var cts))
            cts.Cancel();

        // Pokud je tento model právě načtený do VRAM, uvolni ho — jinak by měl
        // LlamaSharp otevřený file handle a Delete by selhal (file in use)
        var isLoaded = _llama.IsLoaded
                       && string.Equals(_llama.LoadedModelName, model.Name,
                                        StringComparison.OrdinalIgnoreCase);
        if (isLoaded)
        {
            try
            {
                Log.Information("DeleteModel: uvolňuji {Name} z VRAM před smazáním", model.Name);
                await _llama.UnloadModelAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DeleteModel: unload z VRAM selhal, pokračuji v mazání");
            }
        }

        var path = Path.Combine(ModelsDir, model.FileName);
        var deleted = false;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                deleted = true;
                Log.Information("DeleteModel: smazán {Path}", path);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DeleteModel: nelze smazat {Path}", path);
            model.DownloadError = $"Nelze smazat: {ex.Message}";
            return;   // necháváme stav, ať uživatel vidí chybu
        }

        // Pokud byl tento model nastaven jako výchozí v Nastavení, vyčisti ho —
        // jinak by chat dál ukazoval na neexistující soubor a padalo by to při
        // pokusu o načtení modelu.
        if (string.Equals(_settings.Settings.DefaultChatModelName, model.Name,
                          StringComparison.OrdinalIgnoreCase))
        {
            _settings.Settings.DefaultChatModelName = string.Empty;
            try { await _settings.SaveAsync(); }
            catch (Exception ex) { Log.Warning(ex, "DeleteModel: SaveAsync selhalo"); }
        }

        model.IsDownloaded        = false;
        model.IsActive            = false;
        model.DownloadError       = string.Empty;
        model.IsConfirmingDelete  = false;
        model.SizeOnDisk          = string.Empty;

        if (deleted)
            _settings.NotifyModelLibraryChanged();
    }

    // ── Startup scan ──────────────────────────────────────────────────────────
    /// <summary>
    /// Zkontroluje složku Models — označí existující soubory jako stažené,
    /// aktualizuje skutečnou velikost na disku a označí výchozí model jako aktivní.
    /// </summary>
    private void ScanExistingDownloads()
    {
        if (!Directory.Exists(ModelsDir)) return;

        var defaultModel = _settings.Settings.DefaultChatModelName;

        foreach (var model in _allModels)
        {
            if (string.IsNullOrEmpty(model.FileName)) continue;
            var path = Path.Combine(ModelsDir, model.FileName);
            if (!File.Exists(path)) continue;

            model.IsDownloaded = true;
            model.SizeOnDisk   = FormatFileSize(new FileInfo(path).Length);

            // Obnov příznak aktivního modelu z nastavení
            if (!string.IsNullOrEmpty(defaultModel) &&
                string.Equals(model.Name, defaultModel, StringComparison.OrdinalIgnoreCase))
                model.IsActive = true;
        }
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1_024             => $"{bytes} B",
        < 1_048_576         => $"{bytes / 1_024.0:F0} KB",
        < 1_073_741_824     => $"{bytes / 1_048_576.0:F1} MB",
        _                   => $"{bytes / 1_073_741_824.0:F2} GB"
    };

    // ── Katalog modelů ────────────────────────────────────────────────────────
    private static List<ModelItemViewModel> BuildModelCatalog() =>
    [
        // ═══════════════════════ CHAT — LLM GGUF ═══════════════════════════════
        new()
        {
            Name         = "Llama 3.1 8B Instruct Q4_K_M",
            Description  = "Meta Llama 3.1, 8B parametrů. Skvělý poměr výkon/VRAM, češtinu zvládá dobře.",
            Size         = "4.7 GB",  Version = "3.1",
            Category     = ModelCategory.Chat,   Source = ModelSource.HuggingFace,
            VramRequiredGb = 6,     ContextLength = 131072,  Quantization = "Q4_K_M",
            FileName     = "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf",
            DownloadUrl  = "https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF" +
                           "/resolve/main/Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf",
            ModelPageUrl = "https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF",
            IsActive     = true
        },
        new()
        {
            Name         = "Mistral 7B Instruct v0.3 Q4_K_M",
            Description  = "Mistral AI 7B — rychlý, kompaktní, česky zvládá dobře.",
            Size         = "4.1 GB",  Version = "0.3",
            Category     = ModelCategory.Chat,   Source = ModelSource.HuggingFace,
            VramRequiredGb = 5,     ContextLength = 32768,   Quantization = "Q4_K_M",
            FileName     = "Mistral-7B-Instruct-v0.3-Q4_K_M.gguf",
            DownloadUrl  = "https://huggingface.co/bartowski/Mistral-7B-Instruct-v0.3-GGUF" +
                           "/resolve/main/Mistral-7B-Instruct-v0.3-Q4_K_M.gguf",
            ModelPageUrl = "https://huggingface.co/bartowski/Mistral-7B-Instruct-v0.3-GGUF"
        },
        new()
        {
            Name         = "Llama 3.3 70B Instruct Q4_K_M",
            Description  = "Velký Llama 3.3 s 70B parametry. RTX 3090/4090 nebo multi-GPU.",
            Size         = "43 GB",   Version = "3.3",
            Category     = ModelCategory.Chat,   Source = ModelSource.HuggingFace,
            VramRequiredGb = 40,    ContextLength = 131072,  Quantization = "Q4_K_M",
            FileName     = "Llama-3.3-70B-Instruct-Q4_K_M.gguf",
            DownloadUrl  = "https://huggingface.co/bartowski/Llama-3.3-70B-Instruct-GGUF" +
                           "/resolve/main/Llama-3.3-70B-Instruct-Q4_K_M.gguf",
            ModelPageUrl = "https://huggingface.co/bartowski/Llama-3.3-70B-Instruct-GGUF"
        },
        new()
        {
            Name         = "Gemma 3 27B Instruct Q4_K_M",
            Description  = "Google Gemma 3, 27B parametrů. Silný v češtině i kódu.",
            Size         = "16 GB",   Version = "3",
            Category     = ModelCategory.Chat,   Source = ModelSource.HuggingFace,
            VramRequiredGb = 18,    ContextLength = 131072,  Quantization = "Q4_K_M",
            FileName     = "gemma-3-27b-it-Q4_K_M.gguf",
            DownloadUrl  = "https://huggingface.co/bartowski/gemma-3-27b-it-GGUF" +
                           "/resolve/main/gemma-3-27b-it-Q4_K_M.gguf",
            ModelPageUrl = "https://huggingface.co/bartowski/gemma-3-27b-it-GGUF"
        },
        new()
        {
            Name         = "Qwen 2.5 14B Instruct Q4_K_M",
            Description  = "Alibaba Qwen 2.5, 14B. Vynikající výsledky v češtině a programování.",
            Size         = "8.2 GB",  Version = "2.5",
            Category     = ModelCategory.Chat,   Source = ModelSource.HuggingFace,
            VramRequiredGb = 10,    ContextLength = 32768,   Quantization = "Q4_K_M",
            FileName     = "Qwen2.5-14B-Instruct-Q4_K_M.gguf",
            DownloadUrl  = "https://huggingface.co/bartowski/Qwen2.5-14B-Instruct-GGUF" +
                           "/resolve/main/Qwen2.5-14B-Instruct-Q4_K_M.gguf",
            ModelPageUrl = "https://huggingface.co/bartowski/Qwen2.5-14B-Instruct-GGUF"
        },
        new()
        {
            Name         = "Phi-4 Q4_K_M",
            Description  = "Microsoft Phi-4, 14B. Mimořádný výkon na menší VRAM, skvělý na kód i logiku.",
            Size         = "8.5 GB",  Version = "4",
            Category     = ModelCategory.Chat,   Source = ModelSource.HuggingFace,
            VramRequiredGb = 10,    ContextLength = 16384,   Quantization = "Q4_K_M",
            FileName     = "phi-4-Q4_K_M.gguf",
            DownloadUrl  = "https://huggingface.co/bartowski/phi-4-GGUF" +
                           "/resolve/main/phi-4-Q4_K_M.gguf",
            ModelPageUrl = "https://huggingface.co/bartowski/phi-4-GGUF"
        },
        new()
        {
            Name         = "Qwen3 8B Q4_K_M",
            Description  = "Alibaba Qwen3, 8B. Podporuje thinking mode (rozšířené uvažování). Rychlý, skvělý na kód i češtinu.",
            Size         = "5.2 GB",  Version = "3",
            Category     = ModelCategory.Chat,   Source = ModelSource.HuggingFace,
            VramRequiredGb = 7,     ContextLength = 40960,   Quantization = "Q4_K_M",
            FileName     = "Qwen3-8B-Q4_K_M.gguf",
            DownloadUrl  = "https://huggingface.co/bartowski/Qwen3-8B-GGUF" +
                           "/resolve/main/Qwen3-8B-Q4_K_M.gguf",
            ModelPageUrl = "https://huggingface.co/bartowski/Qwen3-8B-GGUF"
        },
        new()
        {
            Name         = "Qwen3 14B Q4_K_M",
            Description  = "Alibaba Qwen3, 14B. Vynikající rovnováha výkonu a VRAM. Thinking mode, silná čeština.",
            Size         = "9.0 GB",  Version = "3",
            Category     = ModelCategory.Chat,   Source = ModelSource.HuggingFace,
            VramRequiredGb = 11,    ContextLength = 40960,   Quantization = "Q4_K_M",
            FileName     = "Qwen3-14B-Q4_K_M.gguf",
            DownloadUrl  = "https://huggingface.co/bartowski/Qwen3-14B-GGUF" +
                           "/resolve/main/Qwen3-14B-Q4_K_M.gguf",
            ModelPageUrl = "https://huggingface.co/bartowski/Qwen3-14B-GGUF"
        },
        new()
        {
            Name         = "Qwen3 32B Q4_K_M",
            Description  = "Alibaba Qwen3, 32B vlajkový model. RTX 3090 / 4090 — nejlepší lokální výsledky, thinking mode.",
            Size         = "20 GB",   Version = "3",
            Category     = ModelCategory.Chat,   Source = ModelSource.HuggingFace,
            VramRequiredGb = 22,    ContextLength = 40960,   Quantization = "Q4_K_M",
            FileName     = "Qwen3-32B-Q4_K_M.gguf",
            DownloadUrl  = "https://huggingface.co/bartowski/Qwen3-32B-GGUF" +
                           "/resolve/main/Qwen3-32B-Q4_K_M.gguf",
            ModelPageUrl = "https://huggingface.co/bartowski/Qwen3-32B-GGUF"
        },
        new()
        {
            Name         = "Qwen3 30B-A3B Q4_K_M",
            Description  = "Qwen3 MoE — 30B celkem, jen 3B aktivní parametry. Rychlý jako 3B, výkon blízký 32B. Thinking mode.",
            Size         = "17 GB",   Version = "3",
            Category     = ModelCategory.Chat,   Source = ModelSource.HuggingFace,
            VramRequiredGb = 20,    ContextLength = 40960,   Quantization = "Q4_K_M",
            FileName     = "Qwen3-30B-A3B-Q4_K_M.gguf",
            DownloadUrl  = "https://huggingface.co/bartowski/Qwen3-30B-A3B-GGUF" +
                           "/resolve/main/Qwen3-30B-A3B-Q4_K_M.gguf",
            ModelPageUrl = "https://huggingface.co/bartowski/Qwen3-30B-A3B-GGUF"
        },

        // ═══════════════════════ CHAT — KREATIVNÍ / NSFW ═══════════════════════
        // Modely vhodné pro tvůrčí psaní, román, dialogy, romantiku i explicitní
        // beletrii. Většinou jsou to fine-tuny Mistral nebo Llama base s natrénovanou
        // permisivnější narativní stránkou. Pro češtinu je nejlepší Mistral-Nemo
        // a Mistral Small base; pure-Llama fine-tuny umí česky hůř.
        new()
        {
            Name         = "Mistral Nemo Instruct 2407 Q4_K_M",
            Description  = "12B model od Mistral AI, výborná čeština a tvůrčí psaní. Jen mírně filtrovaný defaultně, narativ zvládne bez problémů.",
            Size         = "7.1 GB",  Version = "2407",
            Category     = ModelCategory.Chat,   Source = ModelSource.HuggingFace,
            VramRequiredGb = 9,     ContextLength = 131072,  Quantization = "Q4_K_M",
            FileName     = "Mistral-Nemo-Instruct-2407-Q4_K_M.gguf",
            DownloadUrl  = "https://huggingface.co/bartowski/Mistral-Nemo-Instruct-2407-GGUF" +
                           "/resolve/main/Mistral-Nemo-Instruct-2407-Q4_K_M.gguf",
            ModelPageUrl = "https://huggingface.co/bartowski/Mistral-Nemo-Instruct-2407-GGUF"
        },
        new()
        {
            Name         = "Magnum v4 22B Q4_K_M",
            Description  = "Mistral Small fine-tune trénovaný na Claude Opus výstupech — prózy a dialogy ve stylu velkého modelu. Vhodný pro román.",
            Size         = "13 GB",   Version = "4",
            Category     = ModelCategory.Chat,   Source = ModelSource.HuggingFace,
            VramRequiredGb = 14,    ContextLength = 32768,   Quantization = "Q4_K_M",
            FileName     = "magnum-v4-22b-Q4_K_M.gguf",
            DownloadUrl  = "https://huggingface.co/bartowski/magnum-v4-22b-GGUF" +
                           "/resolve/main/magnum-v4-22b-Q4_K_M.gguf",
            ModelPageUrl = "https://huggingface.co/bartowski/magnum-v4-22b-GGUF"
        },
        new()
        {
            Name         = "Cydonia 22B v1 Q4_K_M",
            Description  = "TheDrummer fine-tune Mistral Small. Výslovně uncensored, romantika i explicit. Solidní čeština, výborná angličtina.",
            Size         = "13 GB",   Version = "1",
            Category     = ModelCategory.Chat,   Source = ModelSource.HuggingFace,
            VramRequiredGb = 14,    ContextLength = 32768,   Quantization = "Q4_K_M",
            FileName     = "Cydonia-22B-v1-Q4_K_M.gguf",
            DownloadUrl  = "https://huggingface.co/bartowski/Cydonia-22B-v1-GGUF" +
                           "/resolve/main/Cydonia-22B-v1-Q4_K_M.gguf",
            ModelPageUrl = "https://huggingface.co/bartowski/Cydonia-22B-v1-GGUF"
        },
        new()
        {
            Name         = "Lumimaid v0.2 12B Q4_K_M",
            Description  = "NeverSleep fine-tune nad Mistral Nemo. Specializovaný na roleplay a delší prózu, dobré dialogy postav.",
            Size         = "7.1 GB",  Version = "0.2",
            Category     = ModelCategory.Chat,   Source = ModelSource.HuggingFace,
            VramRequiredGb = 9,     ContextLength = 131072,  Quantization = "Q4_K_M",
            FileName     = "Lumimaid-v0.2-12B-Q4_K_M.gguf",
            DownloadUrl  = "https://huggingface.co/bartowski/Lumimaid-v0.2-12B-GGUF" +
                           "/resolve/main/Lumimaid-v0.2-12B-Q4_K_M.gguf",
            ModelPageUrl = "https://huggingface.co/bartowski/Lumimaid-v0.2-12B-GGUF"
        },
        new()
        {
            Name         = "L3 Stheno v3.2 8B Q4_K_M",
            Description  = "Sao10K fine-tune nad Llama 3 8B. Malý, rychlý, hodně permisivní. Pro češtinu slabší než Mistral varianty.",
            Size         = "4.9 GB",  Version = "3.2",
            Category     = ModelCategory.Chat,   Source = ModelSource.HuggingFace,
            VramRequiredGb = 6,     ContextLength = 8192,    Quantization = "Q4_K_M",
            FileName     = "L3-8B-Stheno-v3.2-Q4_K_M.gguf",
            DownloadUrl  = "https://huggingface.co/bartowski/L3-8B-Stheno-v3.2-GGUF" +
                           "/resolve/main/L3-8B-Stheno-v3.2-Q4_K_M.gguf",
            ModelPageUrl = "https://huggingface.co/bartowski/L3-8B-Stheno-v3.2-GGUF"
        },
        new()
        {
            Name         = "Llama 3.3 70B Instruct abliterated Q4_K_M",
            Description  = "Komunitní úprava Llama 3.3 s odstraněnými refuse vektory. Stejně chytrá, mnohem ochotnější. Velký model — vyžaduje VRAM offload na 24 GB GPU.",
            Size         = "43 GB",   Version = "3.3",
            Category     = ModelCategory.Chat,   Source = ModelSource.HuggingFace,
            VramRequiredGb = 40,    ContextLength = 131072,  Quantization = "Q4_K_M",
            FileName     = "Llama-3.3-70B-Instruct-abliterated-Q4_K_M.gguf",
            DownloadUrl  = "https://huggingface.co/mradermacher/Llama-3.3-70B-Instruct-abliterated-GGUF" +
                           "/resolve/main/Llama-3.3-70B-Instruct-abliterated.Q4_K_M.gguf",
            ModelPageUrl = "https://huggingface.co/mradermacher/Llama-3.3-70B-Instruct-abliterated-GGUF"
        },

        // ═══════════════════════ IMAGE — FLUX GGUF ══════════════════════════════
        new()
        {
            Name         = "FLUX.1 Schnell Q4_K_S",
            Description  = "Rychlá GGUF kvantizace FLUX.1 Schnell. 4 kroky stačí, skvělé pro náhledy. Vyžaduje FLUX závislosti (CLIP-L, T5, VAE).",
            Size         = "8.0 GB",  Version = "1.0",
            Category     = ModelCategory.Image,  Source = ModelSource.HuggingFace,
            VramRequiredGb = 10,    Quantization = "Q4_K_S",
            FileName     = "flux1-schnell-Q4_K_S.gguf",
            DownloadUrl  = "https://huggingface.co/city96/FLUX.1-schnell-gguf" +
                           "/resolve/main/flux1-schnell-Q4_K_S.gguf",
            ModelPageUrl = "https://huggingface.co/city96/FLUX.1-schnell-gguf",
            IsActive     = true
        },
        new()
        {
            Name         = "FLUX.1 Dev Q4_K_S",
            Description  = "Hlavní FLUX.1 Dev GGUF — lepší kvalita než Schnell, 20–30 kroků. Vyžaduje FLUX závislosti (CLIP-L, T5, VAE).",
            Size         = "15 GB",   Version = "1.0",
            Category     = ModelCategory.Image,  Source = ModelSource.HuggingFace,
            VramRequiredGb = 18,    Quantization = "Q4_K_S",
            FileName     = "flux1-dev-Q4_K_S.gguf",
            DownloadUrl  = "https://huggingface.co/city96/FLUX.1-dev-gguf" +
                           "/resolve/main/flux1-dev-Q4_K_S.gguf",
            ModelPageUrl = "https://huggingface.co/city96/FLUX.1-dev-gguf"
        },

        // ═══════════════════════ IMAGE — FLUX závislosti ════════════════════════
        // FLUX modely nejsou „all-in-one" jako SDXL — UNET je oddělený od textových
        // enkodérů (CLIP-L + T5) a VAE. Tyto tři soubory jsou společné pro Schnell
        // i Dev a uživatel je musí stáhnout pokud chce FLUX používat.
        new()
        {
            Name         = "FLUX CLIP-L (povinné)",
            Description  = "Textový enkodér CLIP-L pro FLUX. Stáhni před prvním FLUX generováním.",
            Size         = "246 MB",  Version = "1.0",
            Category     = ModelCategory.Image,  Source = ModelSource.HuggingFace,
            VramRequiredGb = 1,     Quantization = "FP16",
            FileName     = "clip_l.safetensors",
            DownloadUrl  = "https://huggingface.co/comfyanonymous/flux_text_encoders" +
                           "/resolve/main/clip_l.safetensors",
            ModelPageUrl = "https://huggingface.co/comfyanonymous/flux_text_encoders"
        },
        new()
        {
            Name         = "FLUX T5 XXL FP8 (povinné)",
            Description  = "Textový enkodér T5-XXL pro FLUX, FP8 kvantizace (5 GB vs. 10 GB FP16). Lepší pro 12+ GB VRAM.",
            Size         = "5.0 GB",  Version = "1.0",
            Category     = ModelCategory.Image,  Source = ModelSource.HuggingFace,
            VramRequiredGb = 6,     Quantization = "FP8",
            FileName     = "t5xxl_fp8_e4m3fn.safetensors",
            DownloadUrl  = "https://huggingface.co/comfyanonymous/flux_text_encoders" +
                           "/resolve/main/t5xxl_fp8_e4m3fn.safetensors",
            ModelPageUrl = "https://huggingface.co/comfyanonymous/flux_text_encoders"
        },
        new()
        {
            Name         = "FLUX VAE (povinné)",
            Description  = "Variational Autoencoder pro FLUX. Stáhni před prvním FLUX generováním.",
            Size         = "335 MB",  Version = "1.0",
            Category     = ModelCategory.Image,  Source = ModelSource.HuggingFace,
            VramRequiredGb = 1,     Quantization = "FP16",
            FileName     = "ae.safetensors",
            DownloadUrl  = "https://huggingface.co/Comfy-Org/Lumina_Image_2.0_Repackaged" +
                           "/resolve/main/split_files/vae/ae.safetensors",
            ModelPageUrl = "https://huggingface.co/Comfy-Org/Lumina_Image_2.0_Repackaged"
        },

        // ═══════════════════════ IMAGE — SDXL SAFETENSORS ═══════════════════════
        new()
        {
            Name         = "Juggernaut XL v9",
            Description  = "Nejpopulárnější SDXL model pro realistické fotografie a portréty.",
            Size         = "6.5 GB",  Version = "9",
            Category     = ModelCategory.Image,  Source = ModelSource.Civitai,
            VramRequiredGb = 8,     Quantization = "FP16",
            FileName     = "juggernautXL_v9.safetensors",
            DownloadUrl  = "https://civitai.com/api/download/models/456194",
            ModelPageUrl = "https://civitai.com/models/133005/juggernaut-xl"
        },
        new()
        {
            Name         = "DreamShaper XL",
            Description  = "Všestranný SDXL — fantasy, portréty, krajiny, stylizované scény.",
            Size         = "6.5 GB",  Version = "2.1",
            Category     = ModelCategory.Image,  Source = ModelSource.Civitai,
            VramRequiredGb = 8,     Quantization = "FP16",
            FileName     = "dreamshaperXL.safetensors",
            DownloadUrl  = "https://civitai.com/api/download/models/351306",
            ModelPageUrl = "https://civitai.com/models/112902/dreamshaper-xl"
        },
        new()
        {
            Name         = "Pony Diffusion XL v6",
            Description  = "Specializovaný SDXL pro anime a stylizovaný obsah. Vyžaduje Civitai účet.",
            Size         = "6.5 GB",  Version = "6",
            Category     = ModelCategory.Image,  Source = ModelSource.Civitai,
            VramRequiredGb = 8,     Quantization = "FP16",
            FileName     = "ponyDiffusionXL_v6.safetensors",
            DownloadUrl  = "https://civitai.com/api/download/models/290640",
            ModelPageUrl = "https://civitai.com/models/257749/pony-diffusion-xl-v6"
        },
    ];
}
