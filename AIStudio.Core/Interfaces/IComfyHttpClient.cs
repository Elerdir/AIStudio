using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Mapování HTTP API ComfyUI serveru — pure I/O, žádný stav, žádný process
/// management. Lze testovat s <see cref="System.Net.Http.HttpMessageHandler"/>
/// mockem.
///
/// Cíle:
///  • Vytáhnout HTTP volání z monstr-velkého <see cref="IComfyService"/>,
///    aby se daly testovat parsování JSON / error handling izolovaně.
///  • Umožnit budoucí přepojení na jiný backend (lokální stable-diffusion-cpp
///    server, vzdálená API) bez dotyku UI vrstvy.
///
/// Port se předává v každém volání — ComfyUI port se může změnit za běhu
/// (uživatel ho upraví v Nastavení), a tato služba je stateless.
/// </summary>
public interface IComfyHttpClient
{
    /// <summary>
    /// True pokud ComfyUI na daném portu odpovídá na <c>/system_stats</c> do 2 sekund.
    /// </summary>
    Task<bool> IsHealthyAsync(int port, CancellationToken ct = default);

    /// <summary>
    /// Vrátí seznam checkpointů které ComfyUI vidí (přes <c>/object_info/CheckpointLoaderSimple</c>).
    /// Při chybě vrací prázdný seznam — caller filtruje a doplňuje vlastní heuristiky
    /// (ControlNet/VAE filtrování v <see cref="IComfyService"/> wrapperu).
    /// </summary>
    Task<IReadOnlyList<string>> GetCheckpointsAsync(int port, CancellationToken ct = default);

    /// <summary>Seznam LoRA souborů, které ComfyUI nabízí přes LoraLoader uzel.</summary>
    Task<IReadOnlyList<string>> GetLorasAsync(int port, CancellationToken ct = default);

    /// <summary>
    /// Zařadí workflow do fronty. Vrací <c>prompt_id</c> pro pozdější dotazování přes
    /// WebSocket / <c>/history</c>. Pokud ComfyUI vrátí 400 (validation error),
    /// vyhodí <see cref="InvalidOperationException"/> s lidsky čitelným popisem
    /// (parsuje <c>node_errors</c>).
    /// </summary>
    Task<string> QueuePromptAsync(
        int                         port,
        Dictionary<string, object>  workflow,
        string                      clientId,
        CancellationToken           ct = default);

    /// <summary>
    /// Nahraje lokální obrázek do ComfyUI <c>/upload/image</c>. Vrací název
    /// souboru v ComfyUI input složce (uživatel ho použije v LoadImage uzlu).
    /// </summary>
    Task<string> UploadImageAsync(int port, string localFilePath, CancellationToken ct = default);

    /// <summary>
    /// Načte výsledek z <c>/history/{promptId}</c>. Pokud ComfyUI hlásí
    /// <c>status_str == "error"</c>, vyhodí <see cref="ComfyExecutionException"/>
    /// s extrahovanou chybovou hláškou — caller v polling smyčce přestane točit.
    /// Vrací null pokud history zatím nemá záznam (job ještě běží).
    /// </summary>
    Task<ComfyGenerationResult?> FetchHistoryResultAsync(
        int               port,
        string            promptId,
        CancellationToken ct = default);

    /// <summary>
    /// Stáhne hotový obrázek z ComfyUI <c>/view</c> jako bytes. Subfolder
    /// a type berou hodnoty z <see cref="ComfyImageRef"/> z history.
    /// </summary>
    Task<byte[]> DownloadImageAsync(
        int               port,
        string            filename,
        string            subfolder = "",
        string            type      = "output",
        CancellationToken ct        = default);

    /// <summary>
    /// Aktuální počet úloh ve frontě (running + pending). 0 = volno.
    /// Při chybě vrací -1 (caller nemá rozhodovat podle stavu).
    /// </summary>
    Task<int> GetQueueDepthAsync(int port, CancellationToken ct = default);

    /// <summary>
    /// POST /free — požádá ComfyUI o uvolnění modelů z VRAM
    /// (<c>unload_models: true</c>, <c>free_memory: true</c>). Po dokončení chat
    /// generování uvolní VRAM pro chat LLM.
    /// </summary>
    Task FreeMemoryAsync(int port, CancellationToken ct = default);
}
