namespace AIStudio.Core.Models;

/// <summary>
/// Informace o dostupné aktualizaci z UpdateHub serveru.
/// </summary>
/// <param name="Version">Verze nové buildy, např. „0.2.1".</param>
/// <param name="DownloadUrl">Přímá URL k instalačnímu souboru (.exe/.pkg/.dmg).</param>
/// <param name="ReleaseNotes">Changelog v Markdownu nebo prostý text.</param>
/// <param name="PublishedAt">Kdy byla verze publikována (může být default pokud server nedodal).</param>
/// <param name="Sha256">SHA-256 hash instalátoru hex stringem (lowercase). Null pokud manifest hash neposkytuje.</param>
/// <param name="IsMandatory">Pokud true, UI by mělo aktualizaci aplikovat bez možnosti odložení (security fix).</param>
public sealed record UpdateInfo(
    string         Version,
    string         DownloadUrl,
    string         ReleaseNotes,
    DateTimeOffset PublishedAt,
    string?        Sha256      = null,
    bool           IsMandatory = false);
