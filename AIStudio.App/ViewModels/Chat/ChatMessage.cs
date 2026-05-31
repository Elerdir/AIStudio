using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels.Chat;

public enum MessageRole { User, Assistant, System }

/// <summary>
/// Jedna zpráva v konverzaci. Text content (User / Assistant / System) je
/// základ; chat → image gen feature přidala možnost, že zpráva nese vygenerovaný
/// obrázek místo (nebo vedle) textu — viz <see cref="ChatMessage.Image.cs"/>.
///
/// <para>Soubor je rozdělen na partial classes podle area, ať nenarůstá na
/// 500+ řádků mega-VM:</para>
/// <list type="bullet">
/// <item><see cref="ChatMessage"/> (tento soubor) — text + edit + copy + DB mapping</item>
/// <item><c>ChatMessage.Image.cs</c> — image properties + zoom/save/open commands</item>
/// <item><c>ChatMessage.Upgrade.cs</c> — chat → image gen upgrade flow</item>
/// </list>
/// </summary>
public partial class ChatMessage : ObservableObject
{
    /// <summary>
    /// Statický accessor k dialog službě. Nastavuje App.axaml.cs po
    /// BuildServiceProvider(). ChatMessage je vytvářen z DB records (FromRecord),
    /// ne přes DI container — statický accessor je pragmatic kompromis,
    /// abychom nemuseli každou instance projíždět DI factory. V testech lze
    /// nahradit mockem (přiřazením vlastní impl).
    /// </summary>
    public static IDialogService? DialogService { get; set; }

    public string      Id        { get; init; } = Guid.NewGuid().ToString();
    public MessageRole Role      { get; init; }
    public DateTime    Timestamp { get; init; } = DateTime.UtcNow;   // UTC, konvertuj při zobrazení

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenHint), nameof(AttachmentPaths), nameof(DisplayText), nameof(HasAttachments))]
    private string _content = string.Empty;

    // ── Přiložené obrázky (uživatelská zpráva) ────────────────────────────────
    //
    // Uživatelské přílohy jsou v Contentu jako markdown ![obrázek](cesta). Pro
    // zobrazení je parsujeme zvlášť (náhledy) a z textu vyřízneme, ať bublina
    // neukazuje syrovou cestu. Persistence zůstává přes Content (žádná DB změna).

    private static readonly System.Text.RegularExpressions.Regex AttachmentRx =
        new(@"!\[[^\]]*\]\(([^)]+)\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Cesty k obrázkům přiloženým uživatelem (parsované z Content).</summary>
    public System.Collections.Generic.IReadOnlyList<string> AttachmentPaths => ParseAttachmentPaths(Content);

    /// <summary>True pokud zpráva nese aspoň jeden přiložený obrázek.</summary>
    public bool HasAttachments => AttachmentPaths.Count > 0;

    /// <summary>Text zprávy bez markdown obrázků — náhledy se renderují zvlášť.</summary>
    public string DisplayText => StripAttachmentMarkdown(Content);

    public static System.Collections.Generic.List<string> ParseAttachmentPaths(string? content)
    {
        var list = new System.Collections.Generic.List<string>();
        if (string.IsNullOrEmpty(content)) return list;
        foreach (System.Text.RegularExpressions.Match m in AttachmentRx.Matches(content))
        {
            var p = m.Groups[1].Value.Trim();
            if (p.Length > 0) list.Add(p);
        }
        return list;
    }

    public static string StripAttachmentMarkdown(string? content)
        => string.IsNullOrEmpty(content) ? string.Empty : AttachmentRx.Replace(content, string.Empty).Trim();

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
        if (DialogService is null) return;

        await DialogService.SetClipboardTextAsync(Content);

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
