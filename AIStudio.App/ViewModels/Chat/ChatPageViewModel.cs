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

namespace AIStudio.App.ViewModels.Chat;

public partial class ChatPageViewModel : ViewModelBase
{
    private readonly ILlamaService    _llama;
    private readonly IChatRepository  _repo;
    private readonly ISettingsService _settings;

    [ObservableProperty] private string                 _inputText            = string.Empty;
    [ObservableProperty] private ConversationViewModel? _selectedConversation;
    [ObservableProperty] private bool                   _isSending;
    [ObservableProperty] private bool                   _isLoading            = true;
    [ObservableProperty] private bool                   _isLoadingModel;
    [ObservableProperty] private string                 _modelStatusText      = string.Empty;
    [ObservableProperty] private bool                   _canRegenerate;
    [ObservableProperty] private bool                   _isSystemPromptVisible;
    [ObservableProperty] private bool                   _isModelLoaded;
    [ObservableProperty] private string                 _loadedModelName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedTokensLabel), nameof(EstimatedTokensPercent), nameof(TokenBarBrush))]
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
            if (conv is null || conv.MaxTokens == 0) return 0;
            return Math.Min(100, EstimatedTokens * 100.0 / conv.MaxTokens);
        }
    }

    /// <summary>Barva token baru: modrá → žlutá (75 %) → červená (90 %).</summary>
    public IBrush TokenBarBrush => EstimatedTokensPercent switch
    {
        >= 90 => new SolidColorBrush(Color.Parse("#EF4444")),
        >= 75 => new SolidColorBrush(Color.Parse("#FBBF24")),
        _     => new SolidColorBrush(Color.Parse("#818CF8")),
    };

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

    /// <summary>Callback pro navigaci na jinou stránku — nastavuje MainWindowViewModel.</summary>
    public Action<NavigationPage>? RequestNavigate { get; set; }

    [RelayCommand]
    private void NavigateToModels() => RequestNavigate?.Invoke(NavigationPage.Models);

    private static readonly IReadOnlyDictionary<string, string> ModelFileNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Llama 3.1 8B Instruct Q4_K_M"]    = "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf",
            ["Mistral 7B Instruct v0.3 Q4_K_M"] = "Mistral-7B-Instruct-v0.3-Q4_K_M.gguf",
            ["Llama 3.3 70B Instruct Q4_K_M"]   = "Llama-3.3-70B-Instruct-Q4_K_M.gguf",
            ["Gemma 3 27B Instruct Q4_K_M"]     = "gemma-3-27b-it-Q4_K_M.gguf",
            ["Qwen 2.5 14B Instruct Q4_K_M"]    = "Qwen2.5-14B-Instruct-Q4_K_M.gguf",
            ["Phi-4 Q4_K_M"]                    = "phi-4-Q4_K_M.gguf",
            // Qwen3
            ["Qwen3 8B Q4_K_M"]                 = "Qwen3-8B-Q4_K_M.gguf",
            ["Qwen3 14B Q4_K_M"]                = "Qwen3-14B-Q4_K_M.gguf",
            ["Qwen3 32B Q4_K_M"]                = "Qwen3-32B-Q4_K_M.gguf",
            ["Qwen3 30B-A3B Q4_K_M"]            = "Qwen3-30B-A3B-Q4_K_M.gguf",
            // Tvůrčí psaní / méně cenzurované fine-tuny
            ["Mistral Nemo Instruct 2407 Q4_K_M"]            = "Mistral-Nemo-Instruct-2407-Q4_K_M.gguf",
            ["Magnum v4 22B Q4_K_M"]                         = "magnum-v4-22b-Q4_K_M.gguf",
            ["Cydonia 22B v1 Q4_K_M"]                        = "Cydonia-22B-v1-Q4_K_M.gguf",
            ["Lumimaid v0.2 12B Q4_K_M"]                     = "Lumimaid-v0.2-12B-Q4_K_M.gguf",
            ["L3 Stheno v3.2 8B Q4_K_M"]                     = "L3-8B-Stheno-v3.2-Q4_K_M.gguf",
            ["Llama 3.3 70B Instruct abliterated Q4_K_M"]    = "Llama-3.3-70B-Instruct-abliterated-Q4_K_M.gguf",
        };

    public ObservableCollection<ConversationViewModel> Conversations { get; } = new();

    // Sledovaná konverzace pro CollectionChanged (kvůli CanRegenerate)
    private ConversationViewModel? _subscribedConv;

    public ChatPageViewModel(ILlamaService llama, IChatRepository repo, ISettingsService settings)
    {
        _llama    = llama;
        _repo     = repo;
        _settings = settings;

        _llama.StatusChanged += status =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ModelStatusText = status;
                IsLoadingModel  = _llama.IsLoadingModel;
                IsModelLoaded   = _llama.IsLoaded;
                LoadedModelName = _llama.LoadedModelName ?? string.Empty;
            });

        // Při změně Conversations přepočítej filtrovaný seznam
        Conversations.CollectionChanged += (_, _) => UpdateFilteredConversations();

        // Když ModelManager stáhne / přidá / smaže model, obnov picker
        _settings.ModelLibraryChanged += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshAvailableModels);
    }

    // ── Startup ────────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
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

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Conversations.Clear();

                if (loaded.Length == 0)
                {
                    var conv = new ConversationViewModel();
                    _ = TrySaveConversationAsync(conv);
                    Conversations.Add(conv);
                }
                else
                {
                    foreach (var (record, messages) in loaded)
                    {
                        var conv = ConversationViewModel.FromRecord(record);
                        foreach (var msg in messages)
                            conv.Messages.Add(ChatMessage.FromRecord(msg));
                        Conversations.Add(conv);
                    }
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
                if (Conversations.Count == 0)
                    Conversations.Add(new ConversationViewModel());
                SelectedConversation = Conversations.FirstOrDefault();
                IsLoading = false;
            });
        }

        RefreshAvailableModels();
    }

    private string GetModelsDirectory()
    {
        var custom = _settings.Settings.ModelsDirectory;
        return string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                           "AIStudio", "Models")
            : custom;
    }

    public void RefreshAvailableModels()
    {
        // Skenování souborového systému v background vlákně — nesmí blokovat UI
        _ = Task.Run(() =>
        {
            var modelsDir = GetModelsDirectory();

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

            Dispatcher.UIThread.Post(() =>
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
            });
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

    private void UpdateEstimatedTokens()
    {
        var conv = SelectedConversation;
        // Hrubý odhad: 1 token ≈ 4 znaky (angličtina); pro češtinu mírně podceněno, ale dostatečné pro UI
        EstimatedTokens = conv is null ? 0 : conv.Messages.Sum(m => m.Content.Length) / 4;
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

        // Plain text: každá zpráva ve formátu „Role: obsah" oddělená prázdným řádkem.
        // Pro export do MD/TXT existuje samostatný command — tohle je rychlá varianta
        // pro paste třeba do Slacku / Notion / mailu.
        var sb = new StringBuilder();
        foreach (var m in conv.Messages)
        {
            var roleLabel = m.Role switch
            {
                MessageRole.User      => "Já",
                MessageRole.Assistant => "Asistent",
                MessageRole.System    => "Systém",
                _                     => "?"
            };
            sb.AppendLine($"{roleLabel}:");
            sb.AppendLine(m.Content);
            sb.AppendLine();
        }

        try
        {
            await clipboard.SetTextAsync(sb.ToString().TrimEnd());
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
            SuggestedFileName = SanitizeFileName(conv.Title),
            DefaultExtension  = "md",
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                new FilePickerFileType("Prostý text") { Patterns = ["*.txt"] },
            ],
        });

        if (result is null) return;

        var path    = result.Path.LocalPath;
        var isMd    = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        var content = isMd ? BuildMarkdownExport(conv) : BuildTextExport(conv);

        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
    }

    private static string BuildMarkdownExport(ConversationViewModel conv)
    {
        var sb  = new StringBuilder();
        var now = DateTime.Now;

        sb.AppendLine($"# {conv.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Model:** {conv.SelectedModelName}  ");
        sb.AppendLine($"**Exportováno:** {now:d. M. yyyy HH:mm}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(conv.SystemPrompt))
        {
            sb.AppendLine("## Systémový prompt");
            sb.AppendLine();
            sb.AppendLine(conv.SystemPrompt.TrimEnd());
            sb.AppendLine();
        }

        foreach (var msg in conv.Messages)
        {
            sb.AppendLine("---");
            sb.AppendLine();

            var roleLabel = msg.Role == MessageRole.User
                ? $"### 👤 Uživatel · {msg.Timestamp:HH:mm}"
                : $"### 🤖 Asistent · {msg.Timestamp:HH:mm}";

            sb.AppendLine(roleLabel);
            sb.AppendLine();
            sb.AppendLine(msg.Content.TrimEnd());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildTextExport(ConversationViewModel conv)
    {
        var sb   = new StringBuilder();
        var line = new string('═', 72);
        var div  = new string('─', 72);
        var now  = DateTime.Now;

        sb.AppendLine($"Chat:        {conv.Title}");
        sb.AppendLine($"Model:       {conv.SelectedModelName}");
        sb.AppendLine($"Exportováno: {now:d. M. yyyy HH:mm}");
        sb.AppendLine(line);
        sb.AppendLine();

        if (!string.IsNullOrEmpty(conv.SystemPrompt))
        {
            sb.AppendLine("[Systémový prompt]");
            sb.AppendLine(div);
            sb.AppendLine(conv.SystemPrompt.TrimEnd());
            sb.AppendLine();
        }

        foreach (var msg in conv.Messages)
        {
            var roleLabel = msg.Role == MessageRole.User ? "Uživatel" : "Asistent";
            sb.AppendLine($"[{roleLabel}]  {msg.Timestamp:HH:mm}");
            sb.AppendLine(div);
            sb.AppendLine(msg.Content.TrimEnd());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string SanitizeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe    = new string(title.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return safe.Length > 60 ? safe[..60] : safe;
    }

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

        var assistantMsg = new ChatMessage { Role = MessageRole.Assistant, Content = "", IsStreaming = true };
        conv.Messages.Add(assistantMsg);

        await GenerateResponseAsync(conv, assistantMsg, cts.Token);
    }

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

        await GenerateResponseAsync(conv, lastMsg, cts.Token);
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

        await GenerateResponseAsync(conv, assistantMsg, cts.Token);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sdílené tělo generování odpovědi — voláno ze čtyř míst (Send, Regenerate, ConfirmEdit, Compare).
    /// Nastaví IsStreaming, streamuje tokeny, uloží zprávu a ošetří všechny výjimky na jednom místě.
    /// </summary>
    private async Task GenerateResponseAsync(
        ConversationViewModel conv,
        ChatMessage           assistantMsg,
        CancellationToken     ct)
    {
        try
        {
            await EnsureModelLoadedAsync(conv, assistantMsg, ct);
            var history = BuildHistory(conv);

            await StreamIntoMessageAsync(
                _llama.ChatAsync(history, conv.MaxTokens, conv.Temperature, ct),
                assistantMsg,
                ct);

            await TrySaveMessageAsync(assistantMsg, conv);
            _ = TrySaveConversationAsync(conv);
        }
        catch (ModelNotAvailableException)
        {
            Dispatcher.UIThread.Post(() => { assistantMsg.IsStreaming = false; assistantMsg.IsError = true; });
        }
        catch (OperationCanceledException)
        {
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
            Log.Error(ex, "Error during AI generation");
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
        var gpuLayers = _settings.Settings.UseGpu ? -1 : 0;
        await _llama.LoadModelAsync(modelPath, conv.SelectedModelName, gpuLayers: gpuLayers, ct: ct);
    }

    /// <summary>
    /// Sestaví historii zpráv pro LlamaSharp.
    /// Pořadí: systémový prompt → zprávy konverzace bez posledního (asistentský placeholder).
    /// </summary>
    private List<(string Role, string Content)> BuildHistory(ConversationViewModel conv)
    {
        var history = new List<(string Role, string Content)>();

        // Systémový prompt: vlastní per-konverzaci prompt má přednost před výchozím
        var sysPrompt = string.IsNullOrWhiteSpace(conv.SystemPrompt)
            ? GetDefaultSystemPrompt()
            : conv.SystemPrompt;

        // Qwen3 thinking mode: /no_think zakáže interní přemýšlení (kratší odpovědi, rychlejší)
        if (IsQwen3Model(conv.SelectedModelName) && !conv.IsThinkingEnabled)
            sysPrompt = "/no_think\n" + sysPrompt;

        if (!string.IsNullOrEmpty(sysPrompt))
            history.Add(("system", sysPrompt));

        // Zprávy konverzace — vše kromě posledního assistant placeholderu
        foreach (var msg in conv.Messages.Take(conv.Messages.Count - 1))
        {
            if (msg.Role is not (MessageRole.User or MessageRole.Assistant or MessageRole.System))
                Log.Warning("BuildHistory: neznámá role {Role} u zprávy {Id}, fallback na 'user'",
                    msg.Role, msg.Id);

            var role = msg.Role switch
            {
                MessageRole.User      => "user",
                MessageRole.Assistant => "assistant",
                MessageRole.System    => "system",
                _                     => "user"
            };
            history.Add((role, msg.Content));
        }

        return history;
    }

    private static bool IsQwen3Model(string name) =>
        name.Contains("qwen3", StringComparison.OrdinalIgnoreCase);

    private static string GetDefaultSystemPrompt() =>
        "Jsi AI asistent. Odpovídáš přesně, stručně a v jazyce uživatele.";

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

        await GenerateResponseAsync(branch, assistantMsg, cts.Token);
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
        var modelsDir = GetModelsDirectory();

        if (ModelFileNames.TryGetValue(modelName, out var fileName))
        {
            var exact = Path.Combine(modelsDir, fileName);
            if (File.Exists(exact)) return exact;
        }

        if (Directory.Exists(modelsDir))
        {
            var safe  = modelName.Replace(" ", "_").Replace("/", "_");
            var found = Directory.EnumerateFiles(modelsDir, "*.gguf", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                    .Contains(safe, StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found;
        }

        return Path.Combine(modelsDir,
            ModelFileNames.GetValueOrDefault(modelName, $"{modelName}.gguf"));
    }
}
