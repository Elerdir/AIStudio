using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Vision-language model pro chat — umožní „vidět" přiložený obrázek: popsat ho,
/// odpovědět na otázku, přečíst text, posoudit obsah. Nejbližší lokální ekvivalent
/// porozumění obrázku v ChatGPT (Stage 3).
///
/// <para>Vlastní malý VLM (Qwen2.5-VL 7B GGUF + mmproj projektor) — oddělený od
/// uživatelova chat modelu (ten typicky není multimodální). Načítá se on-demand,
/// model + mmproj se auto-stáhnou (~6 GB), aby uživatel nemusel nic řešit.</para>
///
/// <para>Implementace běží přes LlamaSharp mtmd API + <c>InteractiveExecutor</c>
/// (streamuje tokeny jako běžný chat).</para>
/// </summary>
public interface IVisionService
{
    /// <summary>True pokud je VLM model i mmproj projektor stažený (lze použít).</summary>
    bool IsModelAvailable(string modelsDir);

    /// <summary>Probíhá inference (model je zaneprázdněn) — VLM je serializovaný.</summary>
    bool IsBusy { get; }

    /// <summary>Probíhá stahování VLM modelu / projektoru.</summary>
    bool IsDownloading { get; }

    /// <summary>Lidský status řádek pro UI během stahování.</summary>
    string DownloadStatusLine { get; }

    /// <summary>
    /// Zajistí přítomnost VLM modelu + mmproj projektoru (idempotentní). Chybějící
    /// stáhne (~6 GB) z veřejného repa, bez nutnosti tokenu. Progress přes
    /// <paramref name="progress"/>.
    /// </summary>
    Task EnsureModelAsync(string modelsDir, string? hfToken,
                          IProgress<DownloadProgressInfo>? progress, CancellationToken ct);

    /// <summary>
    /// Odpoví na <paramref name="question"/> ohledně obrázku <paramref name="imagePath"/>
    /// (popis, OCR, posouzení…). Streamuje odpověď po tokenech jako běžný chat.
    /// Před voláním musí být model dostupný (viz <see cref="EnsureModelAsync"/>) —
    /// jinak vrátí jednu chybovou hlášku.
    /// </summary>
    IAsyncEnumerable<string> DescribeAsync(
        string imagePath, string question, string modelsDir,
        int maxTokens = 512, float temperature = 0.4f, CancellationToken ct = default);
}
