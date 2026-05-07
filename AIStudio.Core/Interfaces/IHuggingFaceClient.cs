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

    /// <summary>
    /// Obecnější vyhledávání. Na rozdíl od <see cref="SearchGgufModelsAsync"/>
    /// neforsuje filter=gguf — discovery service ho používá pro různé typy
    /// (text-generation, image-to-text, embedding…) a sám si filtruje výsledky.
    /// </summary>
    /// <param name="query">Vyhledávací řetězec; když je prázdný, vrátí top-N.</param>
    /// <param name="filter">HF tag filtr (např. <c>"gguf"</c>, <c>"text-generation"</c>); null = bez filtru.</param>
    /// <param name="task">Pipeline task (<c>"text-generation"</c>, <c>"image-to-text"</c>); null = jakýkoliv.</param>
    /// <param name="sort">Pole pro řazení (<c>"downloads"</c>, <c>"likes"</c>, <c>"trendingScore"</c>).</param>
    /// <param name="direction">-1 = sestupně, 1 = vzestupně.</param>
    Task<IReadOnlyList<HfModelInfo>> SearchModelsAsync(
        string?           query     = null,
        string?           filter    = null,
        string?           task      = null,
        string            sort      = "downloads",
        int               direction = -1,
        int               limit     = 20,
        CancellationToken ct        = default);

    /// <summary>
    /// Načte popis modelu z <c>/api/models/{id}</c>. Vrací krátký výtah z
    /// model card (cardData.summary) nebo prvních ~400 znaků markdown popisu.
    /// </summary>
    Task<string> GetModelDescriptionAsync(string repoId, CancellationToken ct = default);
}
