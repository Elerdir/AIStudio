namespace AIStudio.Core.Services;

/// <summary>
/// Platform-nezávislá sanitizace názvů souborů/složek. Používá PEVNOU sadu
/// „nebezpečných" znaků (Windows superset), ne <see cref="Path.GetInvalidFileNameChars"/>
/// — ten vrací na Unixu/macOS jen <c>'/'</c> a NUL, takže by tam propustil
/// <c>: &lt; &gt; | ? *</c> (nepřenositelné názvy + nedeterministické chování mezi
/// OS, viz padající testy na macOS CI). Výsledek je bezpečný na Windows i macOS.
/// </summary>
public static class FileNameSanitizer
{
    /// <summary>Znaky neplatné v názvu souboru na Windows (superset všech OS).</summary>
    private static readonly char[] Reserved =
        { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    /// <summary>True pro řídicí znaky (&lt;32) a Windows reserved znaky.</summary>
    public static bool IsReserved(char c) => c < 32 || Array.IndexOf(Reserved, c) >= 0;

    /// <summary>
    /// Nahradí nebezpečné znaky <paramref name="replacement"/>. Když je
    /// <paramref name="maxLength"/> &gt; 0, ořízne výsledek na danou délku.
    /// </summary>
    public static string Sanitize(string? name, int maxLength = 0, char replacement = '_')
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var safe = new string(name.Select(c => IsReserved(c) ? replacement : c).ToArray());
        return maxLength > 0 && safe.Length > maxLength ? safe[..maxLength] : safe;
    }
}
