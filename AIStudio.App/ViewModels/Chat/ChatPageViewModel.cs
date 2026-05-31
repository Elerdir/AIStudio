using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Enums;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;

namespace AIStudio.App.ViewModels.Chat;

public partial class ChatPageViewModel : ViewModelBase
{
    private readonly ILlamaService              _llama;
    private readonly IChatRepository            _repo;
    private readonly ISettingsService           _settings;
    private readonly INavigationService         _nav;
    private readonly ISystemPromptPresetService _presetService;
    private readonly ISystemMonitorService?     _monitor;
    // Chat → image gen pipeline. Nullable kvůli starým testovacím konstruktorům
    // a degradaci na chat-only chování, pokud DI tyto služby z nějakého důvodu
    // neposkytne (např. early bootstrap selhání ComfyServices).
    private readonly IChatImageIntentDetector?  _imageIntent;
    private readonly IChatImageOrchestrator?    _imageOrch;

    [ObservableProperty] private string                 _inputText            = string.Empty;
    [ObservableProperty] private ConversationViewModel? _selectedConversation;

    /// <summary>
    /// Předdefinované role/persony — uživatel je vidí jako tlačítka nad systémovým
    /// promptem. Klik naplní SystemPrompt aktuální konverzace.
    ///
    /// Zdroj: <see cref="ISystemPromptPresetService"/> — kombinuje builtin
    /// (Asistent/Editor/Kreativní psaní/Programátor/Brainstorm/Bez instrukcí)
    /// s uživatelskými, které se ukládají do JSON souboru. Builtin presety
    /// jsou available okamžitě po konstruktoru, custom přibydou až po
    /// <see cref="LoadPresetsAsync"/>.
    /// </summary>
    public ObservableCollection<SystemPromptPreset> SystemPromptPresets { get; }

    /// <summary>
    /// Načte custom presety ze souboru a doplní je do <see cref="SystemPromptPresets"/>.
    /// Builtin presety už jsou v kolekci z konstruktoru. Volá se na pozadí
    /// (fire-and-forget) z konstruktoru a kdykoliv po SaveCustom/DeleteCustom.
    /// </summary>
    private async Task LoadPresetsAsync()
    {
        try
        {
            var all = await _presetService.LoadAllAsync();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                SystemPromptPresets.Clear();
                foreach (var p in all) SystemPromptPresets.Add(p);
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ChatPageViewModel: načítání custom presetů selhalo, používám builtin");
        }
    }

    /// <summary>
    /// Naplní SystemPrompt vybrané konverzace zvoleným presetem. Volá UI
    /// po kliku na tlačítko presetu. Pokud není vybraná konverzace, no-op.
    /// </summary>
    [RelayCommand]
    private void ApplySystemPromptPreset(SystemPromptPreset? preset)
    {
        if (preset is null || SelectedConversation is null) return;
        SelectedConversation.SystemPrompt = preset.Prompt;
        _ = TrySaveConversationAsync(SelectedConversation);
    }
    [ObservableProperty] private bool                   _isSending;
    [ObservableProperty] private bool                   _isLoading            = true;
    [ObservableProperty] private bool                   _isLoadingModel;
    [ObservableProperty] private string                 _modelStatusText      = string.Empty;
    [ObservableProperty] private bool                   _canRegenerate;

    // ImageMode + 🎨 toggle + ImageGen orchestration → ChatPageViewModel.ImageGen.cs (partial)

    [ObservableProperty] private bool                   _isSystemPromptVisible;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCpuBackend))]
    private bool _isModelLoaded;
    [ObservableProperty] private string                 _loadedModelName = string.Empty;

    /// <summary>True pokud je načtený model offloadován na GPU (CUDA/Metal).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackendTooltip), nameof(IsCpuBackend), nameof(MemoryUsageLabel))]
    private bool _isGpuBackend;

    // Live RAM/VRAM ticker → ChatPageViewModel.SystemMonitor.cs (partial)

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedTokensLabel), nameof(EstimatedTokensPercent), nameof(TokenBarBrush),
                              nameof(ContextUsageLabel), nameof(ContextUsagePercent), nameof(ContextBarBrush),
                              nameof(ContextUsageTooltip))]
    private int _estimatedTokens;

    public string EstimatedTokensLabel
    {
        get
        {
            var conv = SelectedConversation;
            if (conv is null || EstimatedTokens == 0) return "0";
            return EstimatedTokens < 1_000
                ? $"~{EstimatedTokens} / {conv.MaxTokensLabel}"
                : $"~{EstimatedTokens / 1_000.0:F1}k / {conv.MaxTokensLabel}";
        }
    }

    public double EstimatedTokensPercent
    {
        get
        {
            var conv = SelectedConversation;
            return conv is null
                ? 0
                : AIStudio.Core.Services.TokenEstimator.UsagePercent(EstimatedTokens, conv.MaxTokens);
        }
    }

    /// <summary>Barva token baru: modrá → žlutá (75 %) → červená (90 %).</summary>
    public IBrush TokenBarBrush => EstimatedTokensPercent switch
    {
        >= 90 => new SolidColorBrush(Color.Parse("#EF4444")),
        >= 75 => new SolidColorBrush(Color.Parse("#FBBF24")),
        _     => new SolidColorBrush(Color.Parse("#818CF8")),
    };

    // ── Context window usage (kolik konverzace zaplnila KV cache modelu) ──────
    //
    // Ukazatel je odvozen z AppSettings.ChatContextSize (nastavitelný v Settings).
    // Při překročení modelův LlamaService.ChatAsync sám ořezává nejstarší
    // non-system zprávy, ale uživatel má vidět, kdy se to blíží — proto bar.
    //
    // EstimatedTokens je hrubý odhad (chars/4), ne reálný tokenizer count. Bere se
    // z TokenEstimator.EstimateMessages volaného v UpdateEstimatedTokens(). Stačí
    // to na warning ve 75 % a 90 %.

    /// <summary>Aktuální velikost kontextu z AppSettings (default 8192).</summary>
    public int CurrentContextSize => _settings?.Settings?.ChatContextSize > 0
        ? _settings.Settings.ChatContextSize
        : 8192;

    /// <summary>„~4.2k / 8k tokenů" — pro lidský label v hlavičce chatu.</summary>
    public string ContextUsageLabel
    {
        get
        {
            var ctx       = CurrentContextSize;
            var ctxLabel  = ctx < 10_000 ? $"{ctx / 1_000.0:F1}k" : $"{ctx / 1_000}k";
            if (EstimatedTokens == 0) return $"0 / {ctxLabel}";
            return EstimatedTokens < 1_000
                ? $"~{EstimatedTokens} / {ctxLabel}"
                : $"~{EstimatedTokens / 1_000.0:F1}k / {ctxLabel}";
        }
    }

    /// <summary>Procento využití kontextového okna (0-100). Pro ProgressBar v hlavičce.</summary>
    public double ContextUsagePercent =>
        AIStudio.Core.Services.TokenEstimator.UsagePercent(EstimatedTokens, CurrentContextSize);

    /// <summary>Barva context baru — stejná gradient jako TokenBarBrush.</summary>
    public IBrush ContextBarBrush => ContextUsagePercent switch
    {
        >= 90 => new SolidColorBrush(Color.Parse("#EF4444")),
        >= 75 => new SolidColorBrush(Color.Parse("#FBBF24")),
        _     => new SolidColorBrush(Color.Parse("#818CF8")),
    };

    /// <summary>Vysvětlující tooltip pro context bar — co znamená a kdy se blíží limit.</summary>
    public string ContextUsageTooltip
    {
        get
        {
            var ctx = CurrentContextSize;
            return ContextUsagePercent >= 90
                ? $"Kontext je téměř plný (~{EstimatedTokens} z {ctx} tokenů). " +
                  "Brzy začnu ořezávat nejstarší zprávy. Změnit lze v Nastavení → Velikost kontextu."
                : ContextUsagePercent >= 75
                ? $"Kontext z {Math.Round(ContextUsagePercent)} % zaplněn (~{EstimatedTokens} z {ctx} tokenů)."
                : $"Kontext modelu — ~{EstimatedTokens} z {ctx} tokenů.";
        }
    }

    // ── Title edit state ──────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isEditingTitle;
    [ObservableProperty] private string _editingTitle = string.Empty;

    // ── Model picker ──────────────────────────────────────────────────────────

    // ── Sidebar search + filtered conversations ───────────────────────────────

    [ObservableProperty] private string _sidebarSearch = string.Empty;

    private readonly ObservableCollection<ConversationViewModel> _filteredConversations = new();
    public  ObservableCollection<ConversationViewModel> FilteredConversations => _filteredConversations;

    partial void OnSidebarSearchChanged(string value) => UpdateFilteredConversations();

    private void UpdateFilteredConversations()
    {
        var filter = SidebarSearch.Trim();
        _filteredConversations.Clear();
        foreach (var c in Conversations)
        {
            if (string.IsNullOrEmpty(filter) ||
                c.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.SelectedModelName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                _filteredConversations.Add(c);
        }
    }

    // ── Compare model picker ──────────────────────────────────────────────────

    [ObservableProperty] private bool   _isComparePickerVisible;
    [ObservableProperty] private string _compareModelName = string.Empty;

    // ── Image attachment ──────────────────────────────────────────────────────

    [ObservableProperty] private string _attachedImagePath = string.Empty;
    public bool HasAttachedImage => !string.IsNullOrEmpty(AttachedImagePath);

    partial void OnAttachedImagePathChanged(string value) => OnPropertyChanged(nameof(HasAttachedImage));

    // ── Available models ──────────────────────────────────────────────────────

    public ObservableCollection<string> AvailableModels { get; } = new();

    /// <summary>
    /// True pokud jsou v Models složce skutečné GGUF soubory.
    /// False = uživatel zatím nestáhl žádný model.
    /// </summary>
    [ObservableProperty] private bool _hasDownloadedModels = true; // optimisticky true do prvního scanu

    [RelayCommand]
    private void NavigateToModels() => _nav.Navigate(NavigationPage.Models);

    // Model names/filenames jsou centralizovány v ModelRegistry (AIStudio.Core)
    private static readonly IReadOnlyDictionary<string, string> ModelFileNames =
        ModelRegistry.AsFileNameDictionary();

    public ObservableCollection<ConversationViewModel> Conversations { get; } = new();

    // Sledovaná konverzace pro CollectionChanged (kvůli CanRegenerate)
    private ConversationViewModel? _subscribedConv;

    public ChatPageViewModel(ILlamaService llama, IChatRepository repo, ISettingsService settings,
                             INavigationService nav, ISystemPromptPresetService presetService,
                             ISystemMonitorService?    monitor      = null,
                             IChatImageIntentDetector? imageIntent  = null,
                             IChatImageOrchestrator?   imageOrch    = null)
    {
        _llama         = llama;
        _repo          = repo;
        _settings      = settings;
        _nav           = nav;
        _presetService = presetService;
        _monitor       = monitor;
        _imageIntent   = imageIntent;
        _imageOrch     = imageOrch;

        // Builtin presety jsou immediate-available bez I/O; uživatelské se
        // doplní asynchronně přes LoadPresetsAsync (volá se např. po Load()).
        SystemPromptPresets = new ObservableCollection<SystemPromptPreset>(_presetService.BuiltInPresets);
        _ = LoadPresetsAsync();

        // Live RAM/VRAM ticker — system monitor sbírá metriky každých 2.5 s,
        // přesměrujeme je do badge vedle modelu v chat headeru.
        if (_monitor is not null)
        {
            _monitor.StatusUpdated += OnSystemStatusUpdated;
            // Pokud monitor už něco nasbíral před tím, než jsme se přihlásili
            if (_monitor.Current is not null)
                ApplySystemStatus(_monitor.Current);
        }

        // Reaguj na změnu velikosti kontextu v Nastavení — context bar v hlavičce
        // chatu se přepočítá, jakmile uživatel klikne na jinou hodnotu v ComboBoxu.
        _settings.SettingsSaved += OnSettingsSaved;

        _llama.StatusChanged += status =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ModelStatusText = status;
                IsLoadingModel  = _llama.IsLoadingModel;
                IsModelLoaded   = _llama.IsLoaded;
                LoadedModelName = _llama.LoadedModelName ?? string.Empty;
                IsGpuBackend    = _llama.BackendInfo == "GPU";
            });

        // Při změně Conversations přepočítej filtrovaný seznam
        Conversations.CollectionChanged += (_, _) => UpdateFilteredConversations();

        // Když ModelManager stáhne / přidá / smaže model, obnov picker
        _settings.ModelLibraryChanged += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshAvailableModels);

        // Settings → „Smazat všechny chaty" — vyčisti in-memory ObservableCollection,
        // jinak by uživatel viděl „starý" stav v sidebaru až do restartu aplikace.
        // Invoke (sync) místo Post (async) — jsme stejně už na UI threadu (Settings
        // command continuation se vrací sem); Post by zařadil do queue a vznikla
        // by úzká race window, kdyby uživatel mezitím klikl na „+ Nový chat".
        _settings.ConversationsCleared += () => Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
        {
            Conversations.Clear();
            SelectedConversation = null;
            UpdateFilteredConversations();
            Log.Information("ChatPage: in-memory konverzace vyčištěné po Settings clear");
        });
    }

    // ── Startup ────────────────────────────────────────────────────────────────

    public override async Task InitializeAsync()
    {
        try
        {
            var records = await _repo.LoadAllConversationsAsync();
            Log.Information("ChatPage.Init: načteno {Count} konverzací z DB", records.Count);

            var tasks = records.Select(async r =>
            {
                var msgs = await _repo.LoadMessagesAsync(r.Id);
                return (Record: r, Messages: msgs);
            });
            var loaded = await Task.WhenAll(tasks);

            foreach (var (record, messages) in loaded)
            {
                Log.Information("ChatPage.Init: '{Title}' (id={Id}) — {MsgCount} zpráv",
                                record.Title, record.Id, messages.Count);
            }

            // Naskenuj dostupné modely PŘED nastavením SelectedConversation —
            // jinak má ComboBox prázdné ItemsSource v okamžiku property change
            // a SelectedModelName binding "ztratí" hodnotu (vidí item, který
            // ještě není v kolekci). Uživatelsky to vypadalo, že první chat
            // po startu nemá vybraný model — opravilo se to až přepnutím
            // konverzace a zpět (kdy byl už scan dotypujícího se thread pool dokončen).
            await RefreshAvailableModelsAsync();

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Conversations.Clear();

                // Při prázdné DB (čerstvý install nebo po Settings → smazat všechny
                // chaty) UŽ NEAUTOCREATE prázdnou konverzaci. UI má vlastní empty
                // state (velký „+ Nový chat" CTA v hlavní oblasti) a explicit klik
                // tam dává uživateli kontrolu. Bez tohohle auto-create se po
                // restartu objevoval „Nový chat" jako duch (a uložený rovnou do DB).
                foreach (var (record, messages) in loaded)
                {
                    var conv = ConversationViewModel.FromRecord(record);
                    foreach (var msg in messages)
                        conv.Messages.Add(ChatMessage.FromRecord(msg));
                    Conversations.Add(conv);
                }

                SelectedConversation = Conversations.FirstOrDefault();
                IsLoading = false;
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ChatPage.Init: načtení konverzací z DB selhalo");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // I při chybě nech sidebar prázdný — empty state UI vyzve k akci.
                SelectedConversation = Conversations.FirstOrDefault();
                IsLoading = false;
            });
        }
    }

    /// <summary>
    /// Fire-and-forget verze pro callers, kteří nepotřebují čekat
    /// (např. event handler pro ModelLibraryChanged).
    /// </summary>
    public void RefreshAvailableModels() => _ = RefreshAvailableModelsAsync();

    /// <summary>
    /// Awaitable scan modelů — InitializeAsync čeká, aby ComboBox měl
    /// populated ItemsSource před nastavením SelectedConversation. Bez await
    /// se občas stalo, že první chat po startu vypadal bez modelu.
    /// </summary>
    public async Task RefreshAvailableModelsAsync()
    {
        // Skenování souborového systému v background vlákně — nesmí blokovat UI
        await Task.Run(() =>
        {
            var modelsDir = AIStudio.Core.Services.AppPaths.ResolveModelsDirectory(
                _settings.Settings.ModelsDirectory);

            var fileToName = ModelFileNames.ToDictionary(
                kv => kv.Value.ToLowerInvariant(), kv => kv.Key);

            var found = new List<string>();
            try
            {
                if (Directory.Exists(modelsDir))
                {
                    foreach (var path in Directory.EnumerateFiles(
                                 modelsDir, "*.gguf", SearchOption.AllDirectories))
                    {
                        var fn = Path.GetFileName(path).ToLowerInvariant();

                        // Skip image GGUF (FLUX) — patří do Image Studia, ne sem.
                        // Heuristika podle názvu, dokud nemáme bohatší metadata o souboru.
                        if (fn.StartsWith("flux", StringComparison.OrdinalIgnoreCase) ||
                            fn.Contains("-flux",  StringComparison.OrdinalIgnoreCase) ||
                            fn.StartsWith("sd",   StringComparison.OrdinalIgnoreCase))
                            continue;

                        found.Add(fileToName.TryGetValue(fn, out var name)
                            ? name
                            : Path.GetFileNameWithoutExtension(path));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "RefreshAvailableModels: chyba při čtení složky {Dir}", modelsDir);
            }

            var hasReal = found.Count > 0;
            // Seznam obsahuje POUZE skutečně stažené modely. Bez fallbacku na katalog —
            // pokud není nic staženo, picker zůstane prázdný a uživatel uvidí empty-state
            // s tlačítkem do sekce Modely.
            var list = found.Distinct().OrderBy(x => x).ToList();

            // InvokeAsync místo Post — Post je fire-and-forget a InitializeAsync
            // by mohlo pokračovat dřív, než AvailableModels.Add() doběhne. InvokeAsync
            // vrací Task, který await čeká na dokončení UI update.
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                HasDownloadedModels = hasReal;

                AvailableModels.Clear();
                foreach (var m in list)
                    AvailableModels.Add(m);

                // Pokud byl smazán model, na který odkazuje aktuální konverzace,
                // přepneme ji na první dostupný. Bez toho by SelectedModelName
                // dál ukazoval na neexistující soubor a další pokus o odeslání by
                // padl na ModelNotAvailableException.
                var conv = SelectedConversation;
                if (conv is not null &&
                    !string.IsNullOrEmpty(conv.SelectedModelName) &&
                    !AvailableModels.Contains(conv.SelectedModelName))
                {
                    var replacement = AvailableModels.FirstOrDefault() ?? string.Empty;
                    Log.Information(
                        "RefreshAvailableModels: model '{Old}' už není dostupný, " +
                        "přepínám konverzaci '{Title}' na '{New}'",
                        conv.SelectedModelName, conv.Title, replacement);
                    conv.SelectedModelName = replacement;
                }
            }).GetAwaiter().GetResult();   // Task.Run kontext: bezpečné sync wait, není UI thread
        });
    }

    // ── Property hooks ────────────────────────────────────────────────────────

    /// <summary>Před přepnutím konverzace uložíme draft InputTextu a stav té stávající.</summary>
    partial void OnSelectedConversationChanging(ConversationViewModel? value)
    {
        if (SelectedConversation != null)
        {
            SelectedConversation.Draft = InputText;   // uložit rozepsanou zprávu
            _ = TrySaveConversationAsync(SelectedConversation);
        }
    }

    partial void OnSelectedConversationChanged(ConversationViewModel? value)
    {
        if (value == _subscribedConv) return;

        // Přepneme odběr CollectionChanged + PropertyChanged na novou konverzaci
        if (_subscribedConv != null)
        {
            _subscribedConv.Messages.CollectionChanged -= OnMessagesCollectionChanged;
            _subscribedConv.PropertyChanged            -= OnConvPropertyChanged;
        }
        _subscribedConv = value;
        if (value != null)
        {
            value.Messages.CollectionChanged += OnMessagesCollectionChanged;
            value.PropertyChanged            += OnConvPropertyChanged;
        }

        // Obnov draft rozepsané zprávy pro novou konverzaci
        InputText = value?.Draft ?? string.Empty;

        // Ukaž panel systémového promptu, pokud má konverzace vlastní prompt
        if (value != null && !string.IsNullOrEmpty(value.SystemPrompt))
            IsSystemPromptVisible = true;

        // Skryj compare picker při přepnutí konverzace
        IsComparePickerVisible = false;

        // Auto-uvolnění modelu pokud nová konverzace vyžaduje jiný model
        TryAutoUnloadModel(value);

        // Pokud nová konverzace odkazuje na model, který není v seznamu (např. byl mezitím
        // smazán nebo dostažen), refresh seznamu. Zajistí, že picker bude obsahovat položku
        // odpovídající aktuální hodnotě (jinak by ComboBox vypadal prázdný / zaseknutý).
        if (value is not null
            && !string.IsNullOrEmpty(value.SelectedModelName)
            && !AvailableModels.Contains(value.SelectedModelName))
        {
            RefreshAvailableModels();
        }

        UpdateCanRegenerate();
        UpdateEstimatedTokens();
    }

    /// <summary>Sleduje změny v aktuální konverzaci — při změně modelu auto-uvolní VRAM.</summary>
    private void OnConvPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationViewModel.SelectedModelName))
            TryAutoUnloadModel(SelectedConversation);
    }

    /// <summary>
    /// Uvolní model z VRAM pokud je načten jiný model než ten, který vyžaduje <paramref name="targetConv"/>.
    /// Nemá efekt pokud právě probíhá generování (IsSending = true).
    /// </summary>
    private void TryAutoUnloadModel(ConversationViewModel? targetConv)
    {
        if (!_llama.IsLoaded || IsSending) return;

        var needsModel = targetConv?.SelectedModelName;
        var isMatch    = string.Equals(_llama.LoadedModelName, needsModel,
                                       StringComparison.OrdinalIgnoreCase);
        if (!isMatch)
        {
            Log.Information("Auto-uvolnění modelu '{Name}' (přepnutí konverzace / změna modelu)",
                _llama.LoadedModelName);
            _ = AutoUnloadModelAsync();
        }
    }

    private async Task AutoUnloadModelAsync()
    {
        try
        {
            await _llama.UnloadModelAsync();
            Dispatcher.UIThread.Post(() =>
            {
                IsModelLoaded   = false;
                LoadedModelName = string.Empty;
                IsGpuBackend    = false;
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-uvolnění modelu selhalo");
        }
    }

    partial void OnIsSendingChanged(bool value) => UpdateCanRegenerate();

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => UpdateCanRegenerate();

    private void UpdateCanRegenerate()
    {
        var conv = SelectedConversation;
        CanRegenerate = !IsSending
                     && conv is not null
                     && conv.Messages.Count > 0
                     && conv.Messages[^1].Role == MessageRole.Assistant
                     && !string.IsNullOrEmpty(conv.Messages[^1].Content);
    }

    /// <summary>
    /// Voláno po každém uložení AppSettings — typicky když uživatel
    /// změnil v Nastavení velikost kontextu nebo jiné chat-related option.
    /// Refreshneme context bar bindings (CurrentContextSize a vše navázané).
    /// </summary>
    private void OnSettingsSaved()
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(CurrentContextSize));
            OnPropertyChanged(nameof(ContextUsageLabel));
            OnPropertyChanged(nameof(ContextUsagePercent));
            OnPropertyChanged(nameof(ContextBarBrush));
            OnPropertyChanged(nameof(ContextUsageTooltip));
        });
    }

    private void UpdateEstimatedTokens()
    {
        var conv = SelectedConversation;
        EstimatedTokens = conv is null
            ? 0
            : AIStudio.Core.Services.TokenEstimator.EstimateMessages(
                conv.Messages.Select(m => m.Content));
        OnPropertyChanged(nameof(EstimatedTokensLabel));
        OnPropertyChanged(nameof(EstimatedTokensPercent));
    }

    // ── Unload model from VRAM ────────────────────────────────────────────────

    [RelayCommand]
    private async Task UnloadModelAsync()
    {
        if (!_llama.IsLoaded || IsSending) return;
        try
        {
            await _llama.UnloadModelAsync();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsModelLoaded   = false;
                LoadedModelName = string.Empty;
                IsGpuBackend    = false;
            });
            Log.Information("Model manually unloaded from VRAM");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to unload model");
        }
    }

    // ── System prompt toggle ──────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleSystemPrompt() => IsSystemPromptVisible = !IsSystemPromptVisible;

    // ── Title edit commands ────────────────────────────────────────────────────

    [RelayCommand]
    private void BeginEditTitle()
    {
        if (SelectedConversation is null) return;
        EditingTitle    = SelectedConversation.Title;
        IsEditingTitle  = true;
    }

    [RelayCommand]
    private void ConfirmTitleEdit()
    {
        if (!IsEditingTitle) return;
        IsEditingTitle = false;

        if (SelectedConversation is null) return;
        var trimmed = EditingTitle.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        SelectedConversation.Title = trimmed;
        _ = TrySaveConversationAsync(SelectedConversation);
    }

    [RelayCommand]
    private void CancelTitleEdit()
    {
        IsEditingTitle = false;
    }

    // ── Conversation commands ──────────────────────────────────────────────────

    [RelayCommand]
    private void NewConversation()
    {
        // Priorita:
        //   1) právě načtený model (žádný unload nepotřeba — okamžitě použitelný)
        //   2) výchozí model z nastavení (pokud je stažený)
        //   3) první dostupný stažený model
        //   4) fallback na Llama 3.1 8B (nestažený)
        var settingsDefault = _settings.Settings.DefaultChatModelName;

        string defaultModel;
        if (_llama.IsLoaded
            && !string.IsNullOrEmpty(_llama.LoadedModelName)
            && AvailableModels.Contains(_llama.LoadedModelName))
        {
            defaultModel = _llama.LoadedModelName;
        }
        else if (!string.IsNullOrEmpty(settingsDefault) && AvailableModels.Contains(settingsDefault))
        {
            defaultModel = settingsDefault;
        }
        else
        {
            defaultModel = AvailableModels.Count > 0 ? AvailableModels[0] : "Llama 3.1 8B Instruct Q4_K_M";
        }

        var conv = new ConversationViewModel
        {
            Title             = $"Chat {Conversations.Count + 1}",
            SelectedModelName = defaultModel
        };
        _ = TrySaveConversationAsync(conv);
        Conversations.Insert(0, conv);
        ResortConversations();          // zachová pinned konverzace nahoře
        SelectedConversation = conv;
    }

    [RelayCommand]
    private void DeleteConversation(ConversationViewModel conv)
    {
        _ = TryDeleteConversationAsync(conv.Id);

        var idx = Conversations.IndexOf(conv);
        Conversations.Remove(conv);

        if (SelectedConversation == conv)
            SelectedConversation = Conversations.ElementAtOrDefault(Math.Max(0, idx - 1));

        // Po smazání posledního chatu už NEvytváříme automaticky náhradní —
        // chat area zobrazí empty state s pokynem kliknout na „+ Nový chat".
    }

    // ── Copy whole conversation to clipboard ──────────────────────────────────

    /// <summary>Krátký vizuální feedback po stisknutí „Kopírovat celou" — ikona ✓ na 1.5 s.</summary>
    [ObservableProperty] private bool _isConversationCopied;

    [RelayCommand]
    private async Task CopyConversationAsync()
    {
        var conv = SelectedConversation;
        if (conv is null || conv.Messages.Count == 0) return;

        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;

        var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(win)?.Clipboard;
        if (clipboard is null) return;

        // Pure formátování v Core.ConversationExporter (testovatelné). Pro export
        // do MD/TXT existuje samostatný command — tohle je rychlá clipboard varianta.
        var text = AIStudio.Core.Services.ConversationExporter.ToClipboardText(ToExportMessages(conv));

        try
        {
            await clipboard.SetTextAsync(text);
            IsConversationCopied = true;
            await Task.Delay(1500);
            IsConversationCopied = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CopyConversation: clipboard SetTextAsync selhalo");
        }
    }

    // ── Export conversation ───────────────────────────────────────────────────

    [RelayCommand]
    private async Task ExportConversationAsync()
    {
        var conv = SelectedConversation;
        if (conv is null || conv.Messages.Count == 0) return;

        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win })
            return;

        var result = await win.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Exportovat konverzaci",
            SuggestedFileName = AIStudio.Core.Services.ConversationExporter.SanitizeFileName(conv.Title),
            DefaultExtension  = "md",
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                new FilePickerFileType("Prostý text") { Patterns = ["*.txt"] },
            ],
        });

        if (result is null) return;

        var path     = result.Path.LocalPath;
        var isMd     = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        var messages = ToExportMessages(conv);
        var now      = DateTime.Now;

        var content = isMd
            ? AIStudio.Core.Services.ConversationExporter.ToMarkdown(
                conv.Title, conv.SelectedModelName, conv.SystemPrompt, now, messages)
            : AIStudio.Core.Services.ConversationExporter.ToPlainText(
                conv.Title, conv.SelectedModelName, conv.SystemPrompt, now, messages);

        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
    }

    /// <summary>Mapuje UI ChatMessage na primitivní ExportMessage pro Core exportér.</summary>
    private static List<AIStudio.Core.Services.ExportMessage> ToExportMessages(ConversationViewModel conv) =>
        conv.Messages
            .Select(m => new AIStudio.Core.Services.ExportMessage(
                RoleToString(m.Role), m.Content, m.Timestamp))
            .ToList();

    // ── Send message ──────────────────────────────────────────────────────────

    private CancellationTokenSource? _sendCts;

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        var text = InputText.Trim();
        if (string.IsNullOrEmpty(text) || SelectedConversation is null || IsSending)
        {
            Log.Information("SendMessage: bail-out empty={Empty} convNull={ConvNull} sending={Sending}",
                string.IsNullOrEmpty(text), SelectedConversation is null, IsSending);
            return;
        }

        Log.Information("SendMessage: ENTER conv={Id} model={Model} textLen={Len}",
            SelectedConversation.Id, SelectedConversation.SelectedModelName, text.Length);

        using var cts = new CancellationTokenSource();
        _sendCts  = cts;
        IsSending = true;
        InputText = string.Empty;

        var conv = SelectedConversation;

        // Pokud je přiložen obrázek, vlož ho jako markdown obrázek před text
        var content = string.IsNullOrEmpty(AttachedImagePath)
            ? text
            : $"![obrázek]({AttachedImagePath})\n{text}";
        AttachedImagePath = string.Empty;

        var userMsg = new ChatMessage { Role = MessageRole.User, Content = content };
        conv.Messages.Add(userMsg);
        Log.Information("SendMessage: user msg added to UI, calling SaveMessageAsync (orderIndex={Idx})",
            conv.Messages.Count - 1);

        try
        {
            await _repo.SaveMessageAsync(userMsg.ToRecord(conv.Id, conv.Messages.Count - 1));
            Log.Information("SendMessage: user msg uložen úspěšně");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SendMessage: SaveMessageAsync (user) selhalo");
        }

        // Title se z prvního dotazu nepřepisuje — uživatel si ho přejmenovává ručně
        // (F2 nebo ikonkou tužky v hlavičce). UpdatedAt se aktualizuje po dokončení
        // streamu níž přes TrySaveConversationAsync.

        // ── Klasifikace: chat vs image gen ────────────────────────────────────
        // Před LLM voláním zkusíme, jestli uživatel nechce vygenerovat obrázek.
        // Pokud ano, větvíme do image flow (Comfy + galerie); jinak standardní
        // LLM stream. UI override (ImageMode) přebije auto detekci.
        var classifiedIntent = ClassifyIntent(conv, text);
        if (classifiedIntent != ChatImageIntent.Chat && _imageOrch is not null)
        {
            await RunImageGenerationAsync(conv, text, classifiedIntent, cts.Token);
            _sendCts  = null;
            IsSending = false;
            UpdateEstimatedTokens();
            return;
        }

        var assistantMsg = new ChatMessage { Role = MessageRole.Assistant, Content = "", IsStreaming = true };
        conv.Messages.Add(assistantMsg);

        try
        {
            await EnsureModelLoadedAsync(conv, assistantMsg, cts.Token);

            var history = BuildHistory(conv);

            await StreamIntoMessageAsync(
                _llama.ChatAsync(history, conv.MaxTokens, conv.Temperature, cts.Token),
                assistantMsg,
                cts.Token);

            await TrySaveMessageAsync(assistantMsg, conv);
            _ = TrySaveConversationAsync(conv);

            // Auto-rename — fire-and-forget. Triggeruje se jen když má konverzace
            // default title („Chat N") a stačí počet zpráv. Vlastní helper si
            // ohlídá podmínky a tichounce skončí, když nejsou splněné.
            _ = MaybeAutoRenameAsync(conv);
        }
        catch (ModelNotAvailableException)
        {
            Dispatcher.UIThread.Post(() => { assistantMsg.IsStreaming = false; assistantMsg.IsError = true; });
        }
        catch (AIStudio.Core.Models.ModelLoadFailedException loadEx)
        {
            // LLamaSharp se nepovedlo načíst model (nepodporovaná architektura,
            // corrupted soubor, špatný formát…). Zobrazíme user-friendly hint
            // z ClassifyLoadError místo krytického native stack tracu.
            Log.Warning(loadEx, "Load model failed for {Name}", loadEx.ModelName);
            Dispatcher.UIThread.Post(() =>
            {
                assistantMsg.IsStreaming = false;
                assistantMsg.IsError     = true;
                assistantMsg.Content     = $"❌ **{loadEx.ModelName}** se nepodařilo načíst.\n\n{loadEx.Hint}";
            });
            await TrySaveMessageAsync(assistantMsg, conv);
        }
        catch (OperationCanceledException)
        {
            // IsStreaming=false už nahodil StreamIntoMessageAsync.finally
            if (!string.IsNullOrEmpty(assistantMsg.Content))
            {
                assistantMsg.Content += " *(přerušeno)*";
                await TrySaveMessageAsync(assistantMsg, conv);
            }
            else
            {
                conv.Messages.Remove(assistantMsg);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during chat generation");
            Dispatcher.UIThread.Post(() =>
            {
                assistantMsg.IsStreaming = false;
                assistantMsg.IsError     = true;
                assistantMsg.Content     = $"❌ Chyba: {ex.Message}";
            });
            await TrySaveMessageAsync(assistantMsg, conv);
        }
        finally
        {
            _sendCts  = null;
            IsSending = false;
            UpdateEstimatedTokens();
        }
    }

    // Image gen flow → ChatPageViewModel.ImageGen.cs (partial)

    [RelayCommand]
    private void StopGeneration() => _sendCts?.Cancel();

    // ── Regenerate last response ──────────────────────────────────────────────

    [RelayCommand]
    private async Task RegenerateLastResponseAsync()
    {
        var conv = SelectedConversation;
        if (conv is null || IsSending || conv.Messages.Count == 0) return;

        Log.Debug("RegenerateLastResponse: conv={Id} model={Model} msgCount={Count}",
            conv.Id, conv.SelectedModelName, conv.Messages.Count);

        var lastMsg = conv.Messages[^1];
        if (lastMsg.Role != MessageRole.Assistant) return;

        using var cts = new CancellationTokenSource();
        _sendCts  = cts;
        IsSending = true;

        // Vymaž obsah zprávy + nastav streaming flag
        Dispatcher.UIThread.Post(() => { lastMsg.Content = ""; lastMsg.IsStreaming = true; lastMsg.IsError = false; });

        try
        {
            await EnsureModelLoadedAsync(conv, lastMsg, cts.Token);

            var history = BuildHistory(conv);

            await StreamIntoMessageAsync(
                _llama.ChatAsync(history, conv.MaxTokens, conv.Temperature, cts.Token),
                lastMsg,
                cts.Token);

            await TrySaveMessageAsync(lastMsg, conv);
            _ = TrySaveConversationAsync(conv);
        }
        catch (ModelNotAvailableException)
        {
            Dispatcher.UIThread.Post(() => { lastMsg.IsStreaming = false; lastMsg.IsError = true; });
        }
        catch (OperationCanceledException)
        {
            // IsStreaming=false už nahodil StreamIntoMessageAsync.finally
            if (!string.IsNullOrEmpty(lastMsg.Content))
            {
                lastMsg.Content += " *(přerušeno)*";
                await TrySaveMessageAsync(lastMsg, conv);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during regeneration");
            Dispatcher.UIThread.Post(() =>
            {
                lastMsg.IsStreaming = false;
                lastMsg.IsError     = true;
                lastMsg.Content     = $"❌ Chyba při regeneraci: {ex.Message}";
            });
            await TrySaveMessageAsync(lastMsg, conv);
        }
        finally
        {
            _sendCts  = null;
            IsSending = false;
            UpdateEstimatedTokens();
        }
    }

    // ── Edit & Regenerate ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ConfirmEditAsync(ChatMessage msg)
    {
        var conv = SelectedConversation;
        if (conv is null || IsSending) return;

        var msgIdx = conv.Messages.IndexOf(msg);
        if (msgIdx < 0) return;

        // Ulož nový obsah zprávy
        msg.Content   = msg.EditContent;
        msg.IsEditing = false;

        try   { await _repo.SaveMessageAsync(msg.ToRecord(conv.Id, msgIdx)); }
        catch (Exception ex) { Log.Error(ex, "Failed to save edited message"); }

        // Odstraň všechny zprávy za editovanou (asistent + případné další)
        var toRemove = conv.Messages.Skip(msgIdx + 1).ToList();
        foreach (var m in toRemove)
        {
            m.IsEditing = false;   // #5 reset edit stavu před odstraněním
            conv.Messages.Remove(m);
        }

        try   { await _repo.DeleteMessagesFromIndexAsync(conv.Id, msgIdx + 1); }
        catch (Exception ex) { Log.Error(ex, "Failed to delete messages after edit"); }

        // Generuj novou odpověď
        using var cts = new CancellationTokenSource();
        _sendCts  = cts;
        IsSending = true;

        var assistantMsg = new ChatMessage { Role = MessageRole.Assistant, Content = "", IsStreaming = true };
        conv.Messages.Add(assistantMsg);

        try
        {
            await EnsureModelLoadedAsync(conv, assistantMsg, cts.Token);
            var history = BuildHistory(conv);

            await StreamIntoMessageAsync(
                _llama.ChatAsync(history, conv.MaxTokens, conv.Temperature, cts.Token),
                assistantMsg,
                cts.Token);

            await TrySaveMessageAsync(assistantMsg, conv);
            _ = TrySaveConversationAsync(conv);
        }
        catch (ModelNotAvailableException)
        {
            Dispatcher.UIThread.Post(() => { assistantMsg.IsStreaming = false; assistantMsg.IsError = true; });
        }
        catch (OperationCanceledException)
        {
            // IsStreaming=false už nahodil StreamIntoMessageAsync.finally
            if (!string.IsNullOrEmpty(assistantMsg.Content))
            {
                assistantMsg.Content += " *(přerušeno)*";
                await TrySaveMessageAsync(assistantMsg, conv);
            }
            else
            {
                conv.Messages.Remove(assistantMsg);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during edit+regenerate");
            Dispatcher.UIThread.Post(() =>
            {
                assistantMsg.IsStreaming = false;
                assistantMsg.IsError     = true;
                assistantMsg.Content     = $"❌ Chyba: {ex.Message}";
            });
            await TrySaveMessageAsync(assistantMsg, conv);
        }
        finally
        {
            _sendCts  = null;
            IsSending = false;
            UpdateEstimatedTokens();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Spotřebuje stream tokenů a do <paramref name="target"/>.Content je posílá
    /// v dávkách — nejvýše jednou za ~80 ms místo po každém tokenu. Bez throttlu
    /// by se MarkdownViewer re-parsoval 60-80×/s a UI thread by se zahltil
    /// ("Neodpovídá"). IsStreaming zůstává true až do konce → XAML zobrazuje
    /// laciný plain text místo drahého markdownu během streamu.
    ///
    /// Finální flush a IsStreaming=false běží ve finally — i při OperationCancelled
    /// nebo výjimce zůstane v bublině poslední konzistentní stav. Výjimky probublají
    /// dál, kde si je volající chytá vlastními catch bloky.
    /// </summary>
    private static async Task StreamIntoMessageAsync(
        IAsyncEnumerable<string> tokens,
        ChatMessage              target,
        CancellationToken        ct)
    {
        const int FlushIntervalMs = 80;

        var sb       = new StringBuilder(target.Content);
        var lastTick = Environment.TickCount64 - FlushIntervalMs; // první chunk flushne hned

        try
        {
            await foreach (var token in tokens.WithCancellation(ct))
            {
                sb.Append(token);

                var now = Environment.TickCount64;
                if (now - lastTick >= FlushIntervalMs)
                {
                    var snapshot = sb.ToString();
                    Dispatcher.UIThread.Post(() => target.Content = snapshot);
                    lastTick = now;
                }
            }
        }
        finally
        {
            var final = sb.ToString();
            Dispatcher.UIThread.Post(() =>
            {
                target.Content     = final;
                target.IsStreaming = false;
            });
        }
    }

    /// <summary>Zajistí načtení modelu pokud ještě není. Aktualizuje placeholder bublinu.</summary>
    private async Task EnsureModelLoadedAsync(
        ConversationViewModel conv,
        ChatMessage           placeholder,
        CancellationToken     ct)
    {
        if (_llama.IsLoaded && _llama.LoadedModelName == conv.SelectedModelName)
            return;

        var modelPath = GetModelPath(conv.SelectedModelName);
        if (!File.Exists(modelPath))
        {
            // Zobrazíme chybu v bublině — ale NEUKLÁDÁME do DB
            // (po stažení modelu uživatel neuvidí starou chybovou zprávu)
            Dispatcher.UIThread.Post(() =>
                placeholder.Content =
                    $"⚠️ Model **{conv.SelectedModelName}** není stažen. " +
                    $"Stáhni ho v sekci *Modely*.");
            throw new ModelNotAvailableException(conv.SelectedModelName);
        }

        // C10: placeholder bublina zůstane prázdná — loading UX zajišťuje IsLoadingModel strip v Row 3
        var gpuLayers   = _settings.Settings.UseGpu ? -1 : 0;
        var contextSize = _settings.Settings.ChatContextSize;
        await _llama.LoadModelAsync(modelPath, conv.SelectedModelName,
                                    gpuLayers: gpuLayers,
                                    contextSize: contextSize,
                                    ct: ct);
    }

    /// <summary>
    /// Sestaví historii zpráv pro LlamaSharp. Mapuje UI typy (ChatMessage) na
    /// primitivy a deleguje na <see cref="AIStudio.Core.Services.ChatPromptBuilder"/>
    /// (pure logika system promptu + Qwen3 thinking, unit-testovaná v Core).
    /// Pořadí: systémový prompt → zprávy konverzace bez posledního (placeholder).
    /// </summary>
    private List<(string Role, string Content)> BuildHistory(ConversationViewModel conv)
    {
        var prior = conv.Messages
            .Take(conv.Messages.Count - 1)
            .Select(m => (RoleToString(m.Role), m.Content));

        return AIStudio.Core.Services.ChatPromptBuilder.BuildHistory(
            conv.SystemPrompt, conv.SelectedModelName, conv.IsThinkingEnabled, prior);
    }

    private static string RoleToString(MessageRole role) => role switch
    {
        MessageRole.User      => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.System    => "system",
        _                     => "user",
    };

    // ── Clear conversation ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ClearConversationAsync()
    {
        var conv = SelectedConversation;
        if (conv is null || IsSending) return;

        conv.Messages.Clear();
        try   { await _repo.DeleteMessagesFromIndexAsync(conv.Id, 0); }
        catch (Exception ex) { Log.Error(ex, "Failed to clear messages for {Id}", conv.Id); }

        _ = TrySaveConversationAsync(conv);
        UpdateEstimatedTokens();
    }

    // ── Pin conversation ──────────────────────────────────────────────────────

    [RelayCommand]
    private void TogglePinConversation(ConversationViewModel conv)
    {
        conv.IsPinned = !conv.IsPinned;
        ResortConversations();
        _ = TrySaveConversationAsync(conv);
    }

    private void ResortConversations()
    {
        var sorted = Conversations
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.UpdatedAt)
            .ToList();

        for (var i = 0; i < sorted.Count; i++)
        {
            var cur = Conversations.IndexOf(sorted[i]);
            if (cur != i) Conversations.Move(cur, i);
        }

        UpdateFilteredConversations();
    }

    // ── Thinking mode toggle (Qwen3) ──────────────────────────────────────────

    [RelayCommand]
    private void ToggleThinkingMode()
    {
        if (SelectedConversation is null) return;
        SelectedConversation.IsThinkingEnabled = !SelectedConversation.IsThinkingEnabled;
    }

    // ── Branch conversation ───────────────────────────────────────────────────

    [RelayCommand]
    private async Task BranchConversationAsync(ChatMessage fromMessage)
    {
        var conv = SelectedConversation;
        if (conv is null) return;

        var idx = conv.Messages.IndexOf(fromMessage);
        if (idx < 0) return;

        var branch = new ConversationViewModel
        {
            Title             = $"{conv.Title} (větev)",
            SelectedModelName = conv.SelectedModelName,
            MaxTokens         = conv.MaxTokens,
            Temperature       = conv.Temperature,
            SystemPrompt      = conv.SystemPrompt,
        };

        // Zkopíruj zprávy až po (včetně) zvolené
        for (var i = 0; i <= idx; i++)
        {
            var src  = conv.Messages[i];
            var copy = new ChatMessage { Role = src.Role, Content = src.Content };
            branch.Messages.Add(copy);
        }

        await TrySaveConversationAsync(branch);
        for (var i = 0; i < branch.Messages.Count; i++)
            await TrySaveMessageAsync(branch.Messages[i], branch);

        Conversations.Insert(0, branch);
        ResortConversations();
        SelectedConversation = branch;
    }

    // ── Compare with different model ──────────────────────────────────────────

    [RelayCommand]
    private void ToggleComparePicker()
    {
        IsComparePickerVisible = !IsComparePickerVisible;
        if (IsComparePickerVisible && string.IsNullOrEmpty(CompareModelName) && AvailableModels.Count > 0)
            CompareModelName = AvailableModels.FirstOrDefault(m => m != SelectedConversation?.SelectedModelName)
                               ?? AvailableModels[0];
    }

    [RelayCommand]
    private async Task CompareWithModelAsync()
    {
        var conv = SelectedConversation;
        if (conv is null || string.IsNullOrEmpty(CompareModelName)) return;

        IsComparePickerVisible = false;

        // Najdi poslední uživatelskou zprávu
        var lastUserIdx = -1;
        for (var i = conv.Messages.Count - 1; i >= 0; i--)
        {
            if (conv.Messages[i].Role == MessageRole.User) { lastUserIdx = i; break; }
        }
        if (lastUserIdx < 0) return;

        var branch = new ConversationViewModel
        {
            Title             = $"{conv.Title} ↔ {CompareModelName}",
            SelectedModelName = CompareModelName,
            MaxTokens         = conv.MaxTokens,
            Temperature       = conv.Temperature,
            SystemPrompt      = conv.SystemPrompt,
        };

        for (var i = 0; i <= lastUserIdx; i++)
        {
            var src  = conv.Messages[i];
            var copy = new ChatMessage { Role = src.Role, Content = src.Content };
            branch.Messages.Add(copy);
        }

        await TrySaveConversationAsync(branch);
        for (var i = 0; i < branch.Messages.Count; i++)
            await TrySaveMessageAsync(branch.Messages[i], branch);

        Conversations.Insert(0, branch);
        ResortConversations();
        SelectedConversation = branch;

        // Automaticky vygeneruj odpověď ve větvi
        var assistantMsg = new ChatMessage { Role = MessageRole.Assistant, Content = "", IsStreaming = true };
        branch.Messages.Add(assistantMsg);

        using var cts = new CancellationTokenSource();
        _sendCts  = cts;
        IsSending = true;

        try
        {
            await EnsureModelLoadedAsync(branch, assistantMsg, cts.Token);
            var history = BuildHistory(branch);

            await StreamIntoMessageAsync(
                _llama.ChatAsync(history, branch.MaxTokens, branch.Temperature, cts.Token),
                assistantMsg,
                cts.Token);

            await TrySaveMessageAsync(assistantMsg, branch);
            _ = TrySaveConversationAsync(branch);
        }
        catch (ModelNotAvailableException)
        {
            Dispatcher.UIThread.Post(() => { assistantMsg.IsStreaming = false; assistantMsg.IsError = true; });
        }
        catch (OperationCanceledException)
        {
            // IsStreaming=false už nahodil StreamIntoMessageAsync.finally
            if (!string.IsNullOrEmpty(assistantMsg.Content))
            {
                assistantMsg.Content += " *(přerušeno)*";
                await TrySaveMessageAsync(assistantMsg, branch);
            }
            else branch.Messages.Remove(assistantMsg);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during compare generation");
            Dispatcher.UIThread.Post(() =>
            {
                assistantMsg.IsStreaming = false; assistantMsg.IsError = true;
                assistantMsg.Content     = $"❌ Chyba: {ex.Message}";
            });
            await TrySaveMessageAsync(assistantMsg, branch);
        }
        finally
        {
            _sendCts  = null;
            IsSending = false;
            UpdateEstimatedTokens();
        }
    }

    // ── Image attachment ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task AttachImageAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;

        var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Přiložit obrázek",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Obrázky")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp"]
                }
            ],
        });

        if (files.Count > 0)
            AttachedImagePath = files[0].Path.LocalPath;
    }

    [RelayCommand]
    private void RemoveAttachment() => AttachedImagePath = string.Empty;

    private async Task TrySaveConversationAsync(ConversationViewModel conv)
    {
        try
        {
            conv.UpdatedAt = DateTime.UtcNow;
            await _repo.SaveConversationAsync(conv.ToRecord());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save conversation {Id}", conv.Id);
        }
    }

    private async Task TryDeleteConversationAsync(string conversationId)
    {
        try   { await _repo.DeleteConversationAsync(conversationId); }
        catch (Exception ex) { Log.Error(ex, "Failed to delete conversation {Id}", conversationId); }
    }

    private async Task TrySaveMessageAsync(ChatMessage msg, ConversationViewModel conv)
    {
        try
        {
            var idx = conv.Messages.IndexOf(msg);
            if (idx < 0) idx = conv.Messages.Count - 1;
            await _repo.SaveMessageAsync(msg.ToRecord(conv.Id, idx));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save message {Id}", msg.Id);
        }
    }

    private string GetModelPath(string modelName)
    {
        var modelsDir = AIStudio.Core.Services.AppPaths.ResolveModelsDirectory(
            _settings.Settings.ModelsDirectory);
        return ModelPathResolver.Resolve(modelsDir, modelName);
    }

    // ── Auto-rename ────────────────────────────────────────────────────────────
    //
    // Po prvních ~2 zprávách (user + assistant + user + assistant = 4) pošleme
    // krátký prompt LLM s žádostí o 2-4 slovní český title. Triggeruje se jen
    // pro konverzace s default jménem ("Chat 1", "Chat 2"…) — přejmenované
    // ručně uživatelem se nepřepíší.
    //
    // Spouštíme fire-and-forget, takže neblokuje UI po finished streamu.
    // Pokud v mezičase uživatel pošle další zprávu, llama.ChatAsync z autorename
    // zkonkuruje s tou novou — proto děláme malou kontrolu IsSending na začátku
    // (ne uvnitř streamu, protože LlamaSharp inferenci sériový žádný overlap nesnáší).

    /// <summary>
    /// Pokud konverzace má default-formátovaný title („Chat N") a má aspoň
    /// jednu výměnu (user + assistant), pošle LLM krátký prompt na pojmenování
    /// a přepíše Title. Jinak no-op. Pure logika (default-title detekce, prompt,
    /// čištění výstupu) je v <see cref="AIStudio.Core.Services.ChatTitleGenerator"/>.
    /// </summary>
    private async Task MaybeAutoRenameAsync(ConversationViewModel conv)
    {
        // SendMessageAsync nás volá uvnitř svého try bloku, kde IsSending je
        // ještě true (finally se spustí až po dokončení této metody pokud bychom
        // ji nevolali fire-and-forget). Task.Yield() tady přepne na continuation
        // queue, takže než MaybeAutoRenameAsync pokračuje, doběhne SendMessageAsync.finally
        // a IsSending se přepne na false. Bez toho by IsSending guard níž vždy
        // bailnul ihned.
        await Task.Yield();

        Log.Debug("Auto-rename check: conv={Id} msgs={Count} title='{Title}' loaded={Loaded} sending={Sending}",
            conv.Id, conv.Messages.Count, conv.Title, _llama.IsLoaded, IsSending);

        // Trigger po první výměně (1 user + 1 assistant = 2 zprávy). Bez toho
        // by uživatel musel napsat min. dvě zprávy než by se title změnil — moc
        // pozdě pro UX-feeling „chat se sám pojmenoval po první otázce".
        if (conv.Messages.Count < 2) { Log.Debug("Auto-rename: skip (málo zpráv)"); return; }
        if (!AIStudio.Core.Services.ChatTitleGenerator.IsDefaultTitle(conv.Title)) { Log.Debug("Auto-rename: skip (custom title)"); return; }
        if (!_llama.IsLoaded) { Log.Debug("Auto-rename: skip (model neloaded)"); return; }
        if (IsSending) { Log.Debug("Auto-rename: skip (sending — uživatel poslal další zprávu)"); return; }

        try
        {
            var firstUser   = conv.Messages.FirstOrDefault(m => m.Role == MessageRole.User)?.Content      ?? "";
            var firstAssist = conv.Messages.FirstOrDefault(m => m.Role == MessageRole.Assistant)?.Content ?? "";

            var prompt = AIStudio.Core.Services.ChatTitleGenerator.BuildPrompt(firstUser, firstAssist);

            var sb = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await foreach (var token in _llama.ChatAsync(prompt, maxTokens: 30, temperature: 0.3f, cts.Token))
            {
                sb.Append(token);
                // Hard cap — ochrana proti modelu, který by chtěl vyprávět příběh
                if (sb.Length > 100) break;
            }

            // Čištění LLM výstupu (uvozovky, prefixy, délka) je pure logika v Core.
            var title = AIStudio.Core.Services.ChatTitleGenerator.CleanResponse(sb.ToString());
            if (title is null) return;

            Log.Information("Auto-rename: '{Old}' → '{New}' (conv {Id})", conv.Title, title, conv.Id);

            Dispatcher.UIThread.Post(() => conv.Title = title);
            await TrySaveConversationAsync(conv);
        }
        catch (OperationCanceledException) { /* timeout — ok */ }
        catch (Exception ex)
        {
            Log.Warning(ex, "MaybeAutoRename: selhalo pro {Conv}", conv.Id);
        }
    }
}
