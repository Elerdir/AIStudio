using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

public interface ILlamaService : IAsyncDisposable
{
    string? LoadedModelName { get; }
    /// <summary>"GPU", "CPU", nebo prázdný řetězec před prvním načtením modelu.</summary>
    string  BackendInfo     { get; }
    bool    IsLoaded        { get; }
    bool    IsLoadingModel  { get; }
    bool    UseGpu          { get; set; }

    /// <summary>
    /// Detekovaná GPU (vendor, model, VRAM, doporučený backend) — výsledek
    /// jednorázové <see cref="IGpuDetector"/> detekce při prvním načtení modelu.
    /// <c>null</c> dokud uživatel nenačte aspoň jeden model.
    /// </summary>
    Gpu? DetectedGpu { get; }

    /// <summary>
    /// Vyvolá se při změně stavu: načítání modelu, dokončení, chyba.
    /// Parametr je krátký popisný text vhodný pro status bar.
    /// </summary>
    event Action<string>? StatusChanged;

    /// <summary>
    /// Načte GGUF model do paměti.
    /// </summary>
    /// <param name="contextSize">
    /// Velikost KV cache v tokenech. Default 8192 (kompromis VRAM × délka konverzace).
    /// Uživatelská hodnota se ukládá v <c>AppSettings.ChatContextSize</c>.
    /// </param>
    Task LoadModelAsync(string modelPath, string modelName,
                        int gpuLayers = -1, int contextSize = 8192,
                        CancellationToken ct = default);
    Task UnloadModelAsync();

    IAsyncEnumerable<string> ChatAsync(
        IReadOnlyList<(string Role, string Content)> messages,
        int maxTokens = 2048,
        float temperature = 0.7f,
        CancellationToken ct = default);
}
