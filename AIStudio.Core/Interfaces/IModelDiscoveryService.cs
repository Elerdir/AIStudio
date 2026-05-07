using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Aggregator nad HF + Civitai. Drží curated Picks a umí vrátit top-N
/// modelů pro každý Pick (live z API, s kratičkou cache, aby se neflákaly
/// requesty při každém otevření tabu).
/// </summary>
public interface IModelDiscoveryService
{
    /// <summary>
    /// Statický seznam Picks zobrazených v tabu „Doporučené".
    /// Pořadí se zachovává v UI.
    /// </summary>
    IReadOnlyList<ModelPick> Picks { get; }

    /// <summary>
    /// Pro daný Pick vrátí top-N reálných modelů z odpovídajícího API.
    /// Cachuje na 24 h v procesu (in-memory) — pro forcerefresh viz
    /// <paramref name="bypassCache"/>.
    /// </summary>
    Task<IReadOnlyList<DiscoveredModel>> FetchPickAsync(
        ModelPick         pick,
        bool              bypassCache = false,
        CancellationToken ct          = default);

    /// <summary>
    /// Free-form search napříč zvoleným providerem — pro tab „Hledat".
    /// Pro NSFW/baseModel filtry u Civitai použít přímo
    /// <see cref="ICivitaiClient.SearchAsync"/>.
    /// </summary>
    Task<IReadOnlyList<DiscoveredModel>> SearchAsync(
        PickProvider      provider,
        string            query,
        PickKind?         kind        = null,
        bool              includeNsfw = false,
        int               limit       = 30,
        CancellationToken ct          = default);
}
