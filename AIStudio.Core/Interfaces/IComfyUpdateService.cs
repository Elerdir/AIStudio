namespace AIStudio.Core.Interfaces;

/// <summary>
/// Řízená aktualizace ComfyUI na <b>ověřenou</b> verzi (ne bleeding edge). Sladí lokální
/// git checkout ComfyUI s pinned referencí (<c>ComfyVersion.TestedVersion</c>), reinstaluje
/// <c>requirements.txt</c> a nechá UI restartovat proces. Defenzivní — když instalace není git
/// repozitář nebo chybí <c>git</c>, vrátí jasnou chybu a nic nedělá.
/// </summary>
public interface IComfyUpdateService
{
    /// <summary>True když je v dané složce git repozitář (jde aktualizovat přes git checkout).</summary>
    bool IsGitRepo(string? comfyUiDir);

    /// <summary>True když je v systému dostupný <c>git</c> (na PATH).</summary>
    bool IsGitAvailable();

    /// <summary>
    /// True když je ComfyUI nainstalované, ale <b>není</b> to git repo (typicky Windows
    /// portable .7z) — pak nejde aktualizovat git checkoutem, ale re-extrakcí přes
    /// <see cref="UpdatePortableToLatestAsync"/>.
    /// </summary>
    bool IsPortableInstall(string? comfyUiDir);

    /// <summary>
    /// Aktualizuje portable instalaci na <b>nejnovější</b> ComfyUI: zastaví proces, stáhne
    /// nejnovější portable a rozbalí ho přes stávající (modely/custom_nodes/output zůstanou).
    /// Proces <b>nerestartuje</b> — to nechá na volajícím (UI). Vrací jasnou chybu, když to
    /// nejde (neobvyklé umístění, chybí installer apod.).
    /// </summary>
    Task<ComfyUpdateResult> UpdatePortableToLatestAsync(
        IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Zastaví ComfyUI, přepne git na <paramref name="targetVersion"/> (zkusí <c>vX.Y.Z</c> i
    /// <c>X.Y.Z</c>), reinstaluje <c>requirements.txt</c>. Proces <b>nerestartuje</b> — to nechá
    /// na volajícím (UI). Vše krokově hlásí přes <paramref name="progress"/>.
    /// </summary>
    Task<ComfyUpdateResult> UpdateToVersionAsync(
        string targetVersion, IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>Výsledek řízené aktualizace ComfyUI.</summary>
public sealed record ComfyUpdateResult(bool Success, string Message);
