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
    /// Zastaví ComfyUI, přepne git na <paramref name="targetVersion"/> (zkusí <c>vX.Y.Z</c> i
    /// <c>X.Y.Z</c>), reinstaluje <c>requirements.txt</c>. Proces <b>nerestartuje</b> — to nechá
    /// na volajícím (UI). Vše krokově hlásí přes <paramref name="progress"/>.
    /// </summary>
    Task<ComfyUpdateResult> UpdateToVersionAsync(
        string targetVersion, IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>Výsledek řízené aktualizace ComfyUI.</summary>
public sealed record ComfyUpdateResult(bool Success, string Message);
