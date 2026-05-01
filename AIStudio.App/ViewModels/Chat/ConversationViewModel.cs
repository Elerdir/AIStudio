using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels.Chat;

public partial class ConversationViewModel : ViewModelBase
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [ObservableProperty] private string   _title             = "Nový chat";
    [ObservableProperty] private string   _systemPrompt      = string.Empty;
    [ObservableProperty] private bool     _isModelLoaded;
    [ObservableProperty] private DateTime _createdAt         = DateTime.UtcNow;
    [ObservableProperty] private DateTime _updatedAt         = DateTime.UtcNow;

    // ── Per-conversation state ─────────────────────────────────────────────────

    /// <summary>Rozepsaná zpráva — obnoví se při přepnutí konverzace zpět.</summary>
    [ObservableProperty] private string _draft = string.Empty;

    /// <summary>Připnuto nahoře v sidebaru.</summary>
    [ObservableProperty] private bool _isPinned;

    /// <summary>Thinking mode pro Qwen3 modely (výchozí: zapnuto).</summary>
    [ObservableProperty] private bool _isThinkingEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenSliderValue), nameof(MaxTokensLabel))]
    private int _maxTokens = 4096;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenSliderValue), nameof(MaxTokensLabel), nameof(IsQwen3Selected))]
    private string _selectedModelName = "Llama 3.1 8B Instruct Q4_K_M";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemperatureLabel))]
    private float _temperature = 0.7f;

    /// <summary>Popis teploty pro UI (jedno desetinné místo).</summary>
    public string TemperatureLabel => $"{Temperature:F1}";

    /// <summary>True pokud je vybraný model ze série Qwen3 (zobrazí Thinking mode toggle).</summary>
    public bool IsQwen3Selected =>
        SelectedModelName.Contains("qwen3", StringComparison.OrdinalIgnoreCase);

    /// <summary>Paměť tokenů pro každý model — klíč: název modelu, hodnota: poslední MaxTokens.</summary>
    private readonly Dictionary<string, int> _modelTokenMemory = new();

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    // ── Prázdný stav ──────────────────────────────────────────────────────────

    public bool HasNoMessages => Messages.Count == 0;
    public bool HasMessages   => Messages.Count > 0;

    /// <summary>Celkový počet zpráv v konverzaci — zobrazuje se v sidebaru.</summary>
    public int MessageCount => Messages.Count;

    public bool IsAnyMessageEditing => Messages.Any(m => m.IsEditing);

    public ConversationViewModel()
    {
        Messages.CollectionChanged += (_, e) =>
        {
            // Přihlásit odběr PropertyChanged pro každou novou zprávu
            if (e.NewItems is not null)
                foreach (ChatMessage msg in e.NewItems)
                    msg.PropertyChanged += OnMessagePropertyChanged;

            // Odhlásit odběr pro odebrané zprávy
            if (e.OldItems is not null)
                foreach (ChatMessage msg in e.OldItems)
                    msg.PropertyChanged -= OnMessagePropertyChanged;

            OnPropertyChanged(nameof(HasNoMessages));
            OnPropertyChanged(nameof(HasMessages));
            OnPropertyChanged(nameof(MessageCount));
            OnPropertyChanged(nameof(IsAnyMessageEditing));
        };
    }

    private void OnMessagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessage.IsEditing))
            OnPropertyChanged(nameof(IsAnyMessageEditing));
    }

    // ── Computed: datum pro sidebar ───────────────────────────────────────────

    public string ShortDate
    {
        get
        {
            var local = UpdatedAt.ToLocalTime();
            if (local.Date == DateTime.Today)              return "Dnes";
            if (local.Date == DateTime.Today.AddDays(-1))  return "Včera";
            return local.ToString("d. M.");
        }
    }

    // ── Token slider (logaritmická škála 256 – 500 000) ───────────────────────

    private const double SliderMin = 0.0;
    private const double SliderMax = 100.0;
    private const int    TokenMin  = 256;
    private const int    TokenMax  = 500_000;

    private static readonly double LogMin = Math.Log(TokenMin);
    private static readonly double LogMax = Math.Log(TokenMax);

    /// <summary>Pozice slideru 0–100 mapovaná logaritmicky na tokeny 256–500 000.</summary>
    public double TokenSliderValue
    {
        get => (Math.Log(Math.Max(TokenMin, MaxTokens)) - LogMin) / (LogMax - LogMin) * SliderMax;
        set
        {
            var raw = (int)Math.Round(Math.Exp(LogMin + value / SliderMax * (LogMax - LogMin)));
            MaxTokens = SnapTokens(Math.Clamp(raw, TokenMin, TokenMax));
        }
    }

    /// <summary>Přívětivý popis počtu tokenů pro zobrazení v UI (např. "4k", "128k").</summary>
    public string MaxTokensLabel => MaxTokens switch
    {
        < 1_000  => $"{MaxTokens}",
        < 10_000 => $"{MaxTokens / 1_000.0:F1}k",
        _        => $"{MaxTokens / 1_000:F0}k"
    };

    /// <summary>Zaokrouhlí tokeny na "pěkná" čísla vhodná pro LLM.</summary>
    private static int SnapTokens(int raw) => raw switch
    {
        <= 512    => (raw / 256) * 256,
        <= 4_096  => (raw / 512) * 512,
        <= 16_384 => (raw / 1_024) * 1_024,
        <= 65_536 => (raw / 2_048) * 2_048,
        <= 131_072 => (raw / 4_096) * 4_096,
        _          => (raw / 8_192) * 8_192
    };

    // ── Per-model token paměť ─────────────────────────────────────────────────

    partial void OnSelectedModelNameChanging(string value)
    {
        // Ulož aktuální MaxTokens pro model, ze kterého odcházíme
        if (!string.IsNullOrEmpty(SelectedModelName))
            _modelTokenMemory[SelectedModelName] = MaxTokens;
    }

    partial void OnSelectedModelNameChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;

        // Obnov tokeny pro nový model (z paměti nebo výchozí)
        MaxTokens = _modelTokenMemory.TryGetValue(value, out var saved)
            ? saved
            : ModelDefaultTokens(value);
    }

    private static int ModelDefaultTokens(string modelName)
    {
        var n = modelName.ToLowerInvariant();
        if (n.Contains("70b") || n.Contains("72b"))  return 4_096;
        if (n.Contains("27b") || n.Contains("32b"))  return 8_192;
        if (n.Contains("14b"))                        return 8_192;
        if (n.Contains("8b")  || n.Contains("7b"))   return 4_096;
        return 4_096; // safe default
    }

    // ── DB mapování ────────────────────────────────────────────────────────────

    public ConversationRecord ToRecord() => new(
        Id,
        Title,
        SelectedModelName,
        MaxTokens,
        SystemPrompt,
        CreatedAt,
        DateTime.UtcNow,
        IsPinned,
        Temperature,
        IsThinkingEnabled,
        Draft);

    public static ConversationViewModel FromRecord(ConversationRecord r) => new()
    {
        Id                 = r.Id,
        Title              = r.Title,
        SelectedModelName  = r.ModelName,
        MaxTokens          = r.MaxTokens,
        SystemPrompt       = r.SystemPrompt,
        CreatedAt          = r.CreatedAt,
        UpdatedAt          = r.UpdatedAt,
        IsPinned           = r.IsPinned,
        Temperature        = r.Temperature,
        IsThinkingEnabled  = r.IsThinkingEnabled,
        Draft              = r.Draft,
    };
}
