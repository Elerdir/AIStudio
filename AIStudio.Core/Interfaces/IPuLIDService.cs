using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Zajišťuje PuLID-Flux stack pro generování osoby z referenční fotky obličeje
/// BEZ tréninku LoRA (Stage „identita bez tréninku"). Plně automatická instalace —
/// uživatel nemusí nic řešit ručně, stejně jako u Kontextu/Vision.
///
/// <para>Instaluje: custom node <c>ComfyUI_PuLID_Flux_ll</c> (ZIP, bez git),
/// Python deps do ComfyUI embedded Pythonu (insightface 1.0.1 = pure-Python wheel,
/// facexlib, facenet-pytorch, cython, ftfy, timm), PuLID model (~1.1 GB) a
/// InsightFace antelopev2 modely (auto-download + zploštění dvojitého zanoření).
/// EVA-CLIP se dotáhne sám při prvním běhu.</para>
/// </summary>
public interface IPuLIDService
{
    /// <summary>Název PuLID modelu (ComfyUI/models/pulid/).</summary>
    string PulidModelFileName { get; }

    /// <summary>True pokud je celý PuLID stack připraven (node + model + antelopev2).</summary>
    bool IsAvailable();

    /// <summary>Probíhá instalace / stahování PuLID závislostí.</summary>
    bool IsInstalling { get; }

    /// <summary>Lidský status řádek pro UI během instalace.</summary>
    string StatusLine { get; }

    /// <summary>
    /// Zajistí kompletní PuLID stack (idempotentní). Chybějící kusy doinstaluje:
    /// custom node, pip deps, PuLID model, antelopev2. Progress přes
    /// <paramref name="progress"/> (hlavně stahování modelu).
    /// </summary>
    Task EnsureAsync(IProgress<DownloadProgressInfo>? progress, CancellationToken ct);
}
