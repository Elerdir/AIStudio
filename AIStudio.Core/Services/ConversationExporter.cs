using System.Text;

namespace AIStudio.Core.Services;

/// <summary>Jedna zpráva pro export — primitivní projekce UI <c>ChatMessage</c>.</summary>
public sealed record ExportMessage(string Role, string Content, DateTime Timestamp);

/// <summary>
/// Pure formátování konverzace do Markdown / prostého textu / clipboard formátu.
/// Dříve byla logika (BuildMarkdownExport / BuildTextExport / copy formát /
/// SanitizeFileName) v <c>ChatPageViewModel</c> — netestovatelná bez UI.
///
/// <para>Žádná Avalonia / file I/O závislost — caller dodá data jako primitivy
/// a dostane string, který si sám uloží / dá do clipboardu.</para>
/// </summary>
public static class ConversationExporter
{
    private const string Role_User      = "user";
    private const string Role_Assistant = "assistant";
    private const string Role_System    = "system";

    /// <summary>
    /// Rychlý plain-text formát pro clipboard („Já:\nobsah" oddělené prázdným řádkem).
    /// Vhodné pro paste do Slacku / Notion / mailu.
    /// </summary>
    public static string ToClipboardText(IReadOnlyList<ExportMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            sb.AppendLine($"{RoleLabelClipboard(m.Role)}:");
            sb.AppendLine(m.Content);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Markdown export s frontmatter (model, datum), system promptem a zprávami.</summary>
    public static string ToMarkdown(
        string title, string? modelName, string? systemPrompt,
        DateTime exportedAt, IReadOnlyList<ExportMessage> messages)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"**Model:** {modelName}  ");
        sb.AppendLine($"**Exportováno:** {exportedAt:d. M. yyyy HH:mm}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            sb.AppendLine("## Systémový prompt");
            sb.AppendLine();
            sb.AppendLine(systemPrompt.TrimEnd());
            sb.AppendLine();
        }

        foreach (var msg in messages)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            var roleLabel = msg.Role == Role_User
                ? $"### 👤 Uživatel · {msg.Timestamp:HH:mm}"
                : $"### 🤖 Asistent · {msg.Timestamp:HH:mm}";
            sb.AppendLine(roleLabel);
            sb.AppendLine();
            sb.AppendLine(msg.Content.TrimEnd());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Prostý text export s ASCII oddělovači.</summary>
    public static string ToPlainText(
        string title, string? modelName, string? systemPrompt,
        DateTime exportedAt, IReadOnlyList<ExportMessage> messages)
    {
        var sb   = new StringBuilder();
        var line = new string('═', 72);
        var div  = new string('─', 72);

        sb.AppendLine($"Chat:        {title}");
        sb.AppendLine($"Model:       {modelName}");
        sb.AppendLine($"Exportováno: {exportedAt:d. M. yyyy HH:mm}");
        sb.AppendLine(line);
        sb.AppendLine();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            sb.AppendLine("[Systémový prompt]");
            sb.AppendLine(div);
            sb.AppendLine(systemPrompt.TrimEnd());
            sb.AppendLine();
        }

        foreach (var msg in messages)
        {
            var roleLabel = msg.Role == Role_User ? "Uživatel" : "Asistent";
            sb.AppendLine($"[{roleLabel}]  {msg.Timestamp:HH:mm}");
            sb.AppendLine(div);
            sb.AppendLine(msg.Content.TrimEnd());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Očistí název konverzace na bezpečný název souboru (nahradí neplatné znaky
    /// podtržítkem, ořízne na 60 znaků).
    /// </summary>
    public static string SanitizeFileName(string title)
    {
        if (string.IsNullOrEmpty(title)) return "chat";
        // Pevná platform-nezávislá sada (Windows superset) — Path.GetInvalidFileNameChars()
        // je na macOS jen '/' a nechal by ':', '<', '|' (nepřenositelné, padaly testy).
        return FileNameSanitizer.Sanitize(title, maxLength: 60);
    }

    private static string RoleLabelClipboard(string role) => role switch
    {
        Role_User      => "Já",
        Role_Assistant => "Asistent",
        Role_System    => "Systém",
        _              => "?",
    };
}
