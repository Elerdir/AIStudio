using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

public enum ComfyStatus { Stopped, Starting, Running, Error }

public interface IComfyService
{
    // ── Stav ──────────────────────────────────────────────────────────────────
    ComfyStatus Status     { get; }
    bool        IsRunning  { get; }
    string      StatusMessage { get; }

    event Action<ComfyStatus>? StatusChanged;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    /// <summary>Zkontroluje, zda ComfyUI již běží (spuštěno zvenčí), a nastaví stav.</summary>
    Task InitializeAsync();
    Task<bool> StartAsync(CancellationToken ct = default);
    Task StopAsync();

    // ── API ───────────────────────────────────────────────────────────────────
    Task<IReadOnlyList<string>> GetCheckpointsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetLorasAsync(CancellationToken ct = default);
    Task<string>                QueuePromptAsync(Dictionary<string, object> workflow,
                                                 CancellationToken ct = default);
    /// <summary>
    /// Nahraje lokální obrázek do ComfyUI input složky.
    /// Vrací název souboru v ComfyUI (použij v LoadImage uzlu workflow).
    /// </summary>
    Task<string> UploadImageAsync(string localFilePath, CancellationToken ct = default);
    Task<ComfyGenerationResult?> WaitForResultAsync(string promptId,
                                                     IProgress<int>? progress,
                                                     CancellationToken ct);
    Task<byte[]> DownloadImageAsync(string filename, string subfolder = "",
                                    string type = "output",
                                    CancellationToken ct = default);

    /// <summary>
    /// Požádá ComfyUI o uvolnění modelů z VRAM (POST /free, unload_models + free_memory).
    /// Volá se po dokončení generování v chatu, aby se uvolnila VRAM pro chat LLM
    /// (FLUX ~12 GB + 24B LLM se na 24 GB najednou nevejdou). Best-effort — chyba
    /// se zaloguje a spolkne (uvolnění není kritické pro správnost).
    /// </summary>
    Task FreeMemoryAsync(CancellationToken ct = default);
}
