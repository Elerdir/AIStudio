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
    Task<string>                QueuePromptAsync(Dictionary<string, object> workflow,
                                                 CancellationToken ct = default);
    Task<ComfyGenerationResult?> WaitForResultAsync(string promptId,
                                                     IProgress<int>? progress,
                                                     CancellationToken ct);
    Task<byte[]> DownloadImageAsync(string filename, string subfolder = "",
                                    string type = "output",
                                    CancellationToken ct = default);
}
