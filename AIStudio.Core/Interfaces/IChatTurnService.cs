using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Orchestrace jednoho LLM tahu v chatu — vytaženo z <c>ChatPageViewModel</c>, aby šla
/// byznys logika (načtení modelu + sestavení historie + stream odpovědi) testovat bez UI
/// a nebyla duplikovaná napříč Send / Regenerate / Edit / Compare.
///
/// <para>Služba <b>nedělá UI</b> — vrací stream tokenů a vyhazuje výjimky; ViewModel si
/// řeší zápis do bubliny, throttling a chybové hlášky.</para>
/// </summary>
public interface IChatTurnService
{
    /// <summary>
    /// Zajistí, že je v <see cref="ILlamaService"/> načtený model <paramref name="modelName"/>.
    /// Pokud už je načtený, hned se vrátí. Když GGUF soubor neexistuje, vyhodí
    /// <see cref="ModelNotAvailableException"/> (bez zápisu do UI). Jinak model načte podle
    /// nastavení (GPU/kontext).
    /// </summary>
    Task EnsureModelLoadedAsync(string modelName, CancellationToken ct);

    /// <summary>
    /// Sestaví historii (přes <c>ChatPromptBuilder</c>: system prompt + Qwen3 thinking) a
    /// vrátí stream tokenů odpovědi z LLM. Model musí být předtím načtený
    /// (<see cref="EnsureModelLoadedAsync"/>).
    /// </summary>
    IAsyncEnumerable<string> StreamReplyAsync(ChatTurnRequest request, CancellationToken ct);
}
