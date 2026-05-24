using Avalonia.Controls;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels.Chat;

public enum MessageRole { User, Assistant, System }

public partial class ChatMessage : ObservableObject
{
    public string      Id        { get; init; } = Guid.NewGuid().ToString();
    public MessageRole Role      { get; init; }
    public DateTime    Timestamp { get; init; } = DateTime.UtcNow;   // UTC, konvertuj při zobrazení

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenHint))]
    private string _content = string.Empty;

    /// <summary>Hrubý odhad počtu tokenů zprávy pro zobrazení v UI.</summary>
    public string TokenHint
    {
        get
        {
            var t = AIStudio.Core.Services.TokenEstimator.EstimateText(Content);
            return t > 0 ? $"~{t}t" : "";
        }
    }

    // ── Zobrazení času ────────────────────────────────────────────────────────

    /// <summary>Formátovaný čas zprávy v lokálním čase. Dnes = HH:mm, starší = d. M. HH:mm.</summary>
    public string TimeLabel
    {
        get
        {
            var local = Timestamp.ToLocalTime();
            return local.Date == DateTime.Today
                ? local.ToString("HH:mm")
                : local.ToString("d. M. HH:mm");
        }
    }

    // ── Streaming / error state ───────────────────────────────────────────────

    /// <summary>True dokud nedorazí první token — zobrazí animované tečky místo prázdné bubliny.</summary>
    [ObservableProperty] private bool _isStreaming;

    /// <summary>True pokud generování skončilo chybou — zobrazí tlačítko Zkusit znovu.</summary>
    [ObservableProperty] private bool _isError;

    // ── Image generation state ────────────────────────────────────────────────
    //
    // Tahle zpráva může reprezentovat vygenerovaný obrázek (assistant role,
    // chat → image gen flow). Pokud ImagePath != null, UI vykreslí <Image>
    // místo Markdownu; pokud IsImageGenerating == true, UI ukáže "Generuju
    // obrázek…" placeholder místo typing dots.
    //
    // ImageReferencePath je cesta k obrázku, který byl vstupem do img2img
    // follow-up generace (pokud uživatel řekl "udělej to v noci" na poslední
    // obrázek). Slouží hlavně pro audit / regenerování konverzace.

    /// <summary>Cesta k vygenerovanému obrázku (null = klasická text zpráva).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageMessage))]
    private string? _imagePath;

    /// <summary>Reference obrázek pro img2img follow-up (null = txt2img od nuly).</summary>
    [ObservableProperty] private string? _imageReferencePath;

    /// <summary>True dokud běží Comfy generování — UI ukáže placeholder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageMessage))]
    private bool _isImageGenerating;

    /// <summary>True pokud generování obrázku selhalo — UI ukáže Zkusit znovu.</summary>
    [ObservableProperty] private bool _isImageFailed;

    /// <summary>
    /// True pokud tahle zpráva nese obrázek (hotový nebo se generuje). UI
    /// podle toho přepíná mezi text bublinou a image bublinou.
    /// </summary>
    public bool IsImageMessage => !string.IsNullOrEmpty(ImagePath) || IsImageGenerating;

    // ── Edit state ────────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isEditing;
    [ObservableProperty] private string _editContent = string.Empty;

    [RelayCommand]
    private void BeginEdit()
    {
        EditContent = Content;
        IsEditing   = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
    }

    // ── Clipboard ─────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isCopied;

    [RelayCommand]
    private async Task CopyContentAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
            { MainWindow: { } win }) return;

        var clipboard = TopLevel.GetTopLevel(win)?.Clipboard;
        if (clipboard is null) return;

        await clipboard.SetTextAsync(Content);

        // Krátká vizuální zpětná vazba ✓
        IsCopied = true;
        await Task.Delay(1500);
        IsCopied = false;
    }

    // ── DB mapování ───────────────────────────────────────────────────────────

    public static ChatMessage FromRecord(MessageRecord r)
    {
        var role = r.Role.ToLowerInvariant() switch
        {
            "assistant" => MessageRole.Assistant,
            "system"    => MessageRole.System,
            _           => MessageRole.User
        };

        return new ChatMessage
        {
            Id                 = r.Id,
            Role               = role,
            Timestamp          = r.Timestamp,
            Content            = r.Content,
            ImagePath          = r.ImagePath,
            ImageReferencePath = r.ImageReferencePath,
            // Pokud po load existuje ImagePath ale soubor zmizel (uživatel ho smazal
            // mimo aplikaci), UI ukáže "obrázek nenalezen". Stav IsImageGenerating
            // nikdy nenačítáme z DB — v okamžiku načtení už nic neběží.
            // Obnov příznak chyby podle obsahu (pro zprávy uložené v předchozích sezeních)
            IsError            = r.Content.StartsWith("❌") || r.Content.StartsWith("⚠️"),
        };
    }

    public MessageRecord ToRecord(string conversationId, int orderIndex) => new(
        Id,
        conversationId,
        Role.ToString().ToLowerInvariant(),
        Content,
        Timestamp,
        orderIndex,
        ImagePath,
        ImageReferencePath);
}
