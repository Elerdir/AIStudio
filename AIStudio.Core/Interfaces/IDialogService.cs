namespace AIStudio.Core.Interfaces;

/// <summary>
/// Filtr typů souborů pro file pickers — popisek + glob patterny.
/// Příklad: <c>new("Markdown", new[] { "*.md" })</c>.
/// </summary>
public sealed record FileFilter(string Label, IReadOnlyList<string> Patterns);

/// <summary>
/// Abstrakce dialog / clipboard / window operací — eliminuje přímou závislost
/// VMs na Avalonia API. Cíl: VMs jdou unit-testovat bez Avalonia bootstrapu
/// (mocknutí <see cref="IDialogService"/>), a Core/Infrastructure si drží
/// vrstvovou hierarchii bez UI framework závislostí.
///
/// <para>Implementace: <c>AvaloniaDialogService</c> v AIStudio.App vrstvě.
/// Pro VMs vytvořené runtime (např. <c>ChatMessage</c> z DB records, ne přes
/// DI container) se používá static accessor — viz jednotlivá VM třída.</para>
/// </summary>
public interface IDialogService
{
    /// <summary>Zkopíruje text do systémové clipboardy.</summary>
    Task SetClipboardTextAsync(string text);

    /// <summary>
    /// Otevře Save File dialog. Vrátí lokální cestu vybraného souboru,
    /// nebo null pokud uživatel zrušil. Filters jsou v UI prezentovány
    /// jako dropdown — první v seznamu je default.
    /// </summary>
    Task<string?> SaveFileAsync(
        string             title,
        string             suggestedFileName,
        string             defaultExtension,
        IReadOnlyList<FileFilter> filters);

    /// <summary>
    /// Otevře Open File dialog. Vrátí lokální cestu nebo null. AllowMultiple
    /// = false (single file). Pro multi-select použij <see cref="OpenFilesAsync"/>.
    /// </summary>
    Task<string?> OpenFileAsync(
        string             title,
        IReadOnlyList<FileFilter> filters);

    /// <summary>Otevře folder picker. Vrátí lokální cestu složky nebo null.</summary>
    Task<string?> OpenFolderAsync(string title);

    /// <summary>
    /// Otevře full-screen preview obrázku v samostatném okně. Implementace
    /// (Avalonia) vytvoří ImageZoomWindow + zobrazí; nehází, pokud soubor
    /// neexistuje (jen tiše skončí).
    /// </summary>
    void ShowImagePreview(string imagePath);
}
