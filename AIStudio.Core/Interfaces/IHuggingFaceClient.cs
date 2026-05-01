using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Klient pro HuggingFace public API — vyhledávání modelů a listing souborů.
/// </summary>
public interface IHuggingFaceClient
{
    /// <summary>
    /// Vyhledá GGUF modely podle dotazu. Řazeno podle počtu stažení.
    /// </summary>
    /// <param name="query">Vyhledávací dotaz (název modelu, autor, klíčové slovo).</param>
    /// <param name="limit">Maximální počet výsledků (default 20).</param>
    Task<IReadOnlyList<HfModelInfo>> SearchGgufModelsAsync(
        string            query,
        int               limit = 20,
        CancellationToken ct    = default);

    /// <summary>
    /// Vrátí seznam GGUF souborů v daném HuggingFace repu.
    /// </summary>
    Task<IReadOnlyList<HfFileInfo>> ListGgufFilesAsync(
        string            repoId,
        CancellationToken ct = default);

    /// <summary>
    /// Sestaví přímou stahovací URL pro konkrétní soubor v repu (resolve/main).
    /// </summary>
    string BuildDownloadUrl(string repoId, string filePath);

    /// <summary>
    /// URL na webovou stránku modelu (pro tlačítko „Otevřít stránku modelu").
    /// </summary>
    string BuildModelPageUrl(string repoId);
}
