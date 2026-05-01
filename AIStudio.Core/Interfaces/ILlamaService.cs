namespace AIStudio.Core.Interfaces;

public interface ILlamaService : IAsyncDisposable
{
    string? LoadedModelName { get; }
    bool    IsLoaded        { get; }
    bool    IsLoadingModel  { get; }
    bool    UseGpu          { get; set; }

    /// <summary>
    /// Vyvolá se při změně stavu: načítání modelu, dokončení, chyba.
    /// Parametr je krátký popisný text vhodný pro status bar.
    /// </summary>
    event Action<string>? StatusChanged;

    Task LoadModelAsync(string modelPath, string modelName,
                        int gpuLayers = -1, CancellationToken ct = default);
    Task UnloadModelAsync();

    IAsyncEnumerable<string> ChatAsync(
        IReadOnlyList<(string Role, string Content)> messages,
        int maxTokens = 2048,
        float temperature = 0.7f,
        CancellationToken ct = default);
}
