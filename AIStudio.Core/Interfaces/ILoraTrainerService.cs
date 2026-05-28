using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// LoRA trénink na lokálním HW. Wrapper kolem sd-scripts (<c>sdxl_train_network.py</c>
/// pro SDXL, <c>train_network.py</c> pro SD 1.5, <c>flux_train_network.py</c>
/// pro FLUX). Spouští Python subprocess přes ComfyUI venv a parsuje stdout
/// pro progress reporting.
///
/// <para>Předpoklady (kontroluje <see cref="ILoraTrainerDependencyService"/>):</para>
/// <list type="bullet">
/// <item>Existuje ComfyUI Python venv s nainstalovanými pip balíky</item>
/// <item>Existuje sd-scripts repo</item>
/// <item>Base model je stažený a má rozumný formát (.safetensors / .ckpt)</item>
/// </list>
///
/// <para>Reálné runtime požadavky (HW):</para>
/// <list type="bullet">
/// <item>SD 1.5 LoRA: 6+ GB VRAM, 30-60 min na RTX 3090</item>
/// <item>SDXL LoRA: 8+ GB VRAM (s gradient ckpt + 8-bit Adam), 30-50 min na RTX 3090</item>
/// <item>FLUX LoRA: 16+ GB VRAM, 60-120 min na RTX 3090</item>
/// </list>
/// </summary>
public interface ILoraTrainerService
{
    /// <summary>True dokud běží training subprocess (od TrainAsync start do return).</summary>
    bool IsTraining { get; }

    /// <summary>Stdout/stderr Python skriptu — pro debug log v UI.</summary>
    event Action<string>? OutputLog;

    /// <summary>
    /// Provede LoRA trénink podle requestu. Dataset se zkopíruje do dočasné
    /// working dir (s captions jako <c>.txt</c> sidecary), spustí se Python,
    /// po dokončení se LoRA přesune do <c>request.OutputDirectory</c> a
    /// working dir se smaže.
    ///
    /// <para>Pokud uživatel zruší přes <paramref name="ct"/>, subprocess je
    /// terminován a working dir se uklidí.</para>
    /// </summary>
    /// <returns>
    /// <see cref="LoraTrainingResult"/> s cestou k vygenerované LoRA pokud uspěla,
    /// jinak ErrorMessage. Nikdy nehází — selhání se vrací jako Result.
    /// </returns>
    Task<LoraTrainingResult> TrainAsync(
        LoraTrainingRequest               request,
        IProgress<LoraTrainingProgress>?  progress = null,
        CancellationToken                 ct       = default);
}
