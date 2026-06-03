using System.Text.RegularExpressions;

namespace AIStudio.Core.Services;

/// <summary>
/// Čte verzi nainstalovaného ComfyUI ze souboru <c>comfyui_version.py</c>
/// (obsahuje <c>__version__ = "0.3.x"</c>). Pomáhá v Nastavení zobrazit, jaká
/// verze běží, a porovnat ji s verzí, na které je AI Studio testované.
/// </summary>
public static partial class ComfyVersion
{
    [GeneratedRegex(@"__version__\s*=\s*[""']([^""']+)[""']")]
    private static partial Regex VersionRegex();

    /// <summary>
    /// Verze ComfyUI, na které je AI Studio ověřené. Slouží jako „pinned" reference —
    /// dokud běžící verze odpovídá, je vše zaručeně kompatibilní.
    /// </summary>
    public const string TestedVersion = "0.3.40";

    /// <summary>Vyparsuje <c>__version__</c> z obsahu <c>comfyui_version.py</c>. Null když nenalezeno.</summary>
    public static string? Parse(string? fileContent)
    {
        if (string.IsNullOrEmpty(fileContent)) return null;
        var m = VersionRegex().Match(fileContent);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// Přečte verzi z <c>{comfyUiDir}/comfyui_version.py</c>. Vrátí null, když adresář
    /// nebo soubor neexistuje, popř. verzi nelze vyparsovat.
    /// </summary>
    public static string? ReadFromDirectory(string? comfyUiDir)
    {
        if (string.IsNullOrWhiteSpace(comfyUiDir)) return null;
        try
        {
            var file = Path.Combine(comfyUiDir, "comfyui_version.py");
            if (!File.Exists(file)) return null;
            return Parse(File.ReadAllText(file));
        }
        catch
        {
            return null;
        }
    }
}
