using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace AIStudio.App.ViewModels.Lora;

/// <summary>
/// HW indikátor a odhad délky tréninku — detekce GPU/VRAM a varování, když na
/// trénink nestačí. Partial split z hlavního <see cref="LoraTrainingPaneViewModel"/>:
/// jediná část, která sleduje <c>ISystemMonitorService</c>.
/// </summary>
public partial class LoraTrainingPaneViewModel
{
    // ── HW indikátor + odhad ──────────────────────────────────────────────────

    [ObservableProperty] private string _hwInfoLabel = "Detekuji GPU…";
    [ObservableProperty] private bool   _hwSupportsTraining = true;
    [ObservableProperty] private string _hwWarningText = string.Empty;

    /// <summary>Odhad doby tréninku podle rank/steps/VRAM. Pro UX pomocný číselný indikátor.</summary>
    public string EstimatedTimeLabel
    {
        get
        {
            if (string.IsNullOrEmpty(SelectedBaseModel)) return string.Empty;
            // Hrubý odhad: ~3 it/s na RTX 3090 pro SDXL @ rank 32 batch 1 → ~500 s na 1500 stepů.
            // Škálujeme rank lineárně, batch lineárně, model typu (SDXL ~1.0, SD1.5 ~0.4, FLUX ~2.0).
            var modelFactor = BaseModelTypeLabel switch
            {
                "FLUX"   => 2.0,
                "SDXL"   => 1.0,
                _        => 0.4,
            };
            var rankFactor = Rank / 32.0;
            var stepsPerSec = 3.0 / modelFactor / Math.Max(0.6, rankFactor);
            var seconds = Steps / Math.Max(0.5, stepsPerSec);

            // Slabší HW (pod 12 GB VRAM) — 1.5-2× pomalejší
            if (_monitor?.Current?.VramTotalGb < 12) seconds *= 1.8;

            return seconds switch
            {
                < 60   => $"~{seconds:F0} s",
                < 3600 => $"~{seconds / 60:F0} min",
                _      => $"~{seconds / 3600:F1} h",
            };
        }
    }


    /// <summary>
    /// Reaguje na update ze SystemMonitorService — typicky 1× za 2.5 s. Pro UI
    /// nás zajímá hlavně první sample (po něm máme GPU info), pak už změny
    /// VRAM jsou pro náš HW label irelevantní.
    /// </summary>
    private void OnSystemStatusUpdated(object? _, AIStudio.Core.Models.SystemStatus __)
        => Dispatcher.UIThread.Post(DetectHardware);

    /// <summary>
    /// Detekce HW — VRAM size + vendor pro odhad rychlosti a varování.
    /// Volá se při startu (kdy Current je typicky null) a znovu z
    /// <see cref="OnSystemStatusUpdated"/> jakmile přijde první sample.
    /// </summary>
    private void DetectHardware()
    {
        try
        {
            var cur = _monitor?.Current;
            if (cur is null || !cur.GpuAvailable)
            {
                HwInfoLabel        = "Bez GPU — trénink na CPU nedoporučujeme";
                HwSupportsTraining = false;
                HwWarningText      = "Bez GPU bude trénink trvat desítky hodin. Zvol prosím PC s NVIDIA GPU (8+ GB VRAM).";
                return;
            }

            var vramGb = cur.VramTotalGb;
            var name   = cur.GpuName ?? "GPU";

            // Zapamatuj poslední platnou hodnotu — při startu tréninku může být
            // _monitor.Current.VramTotalGb dočasně 0 (mezi vzorky) a bez cache by
            // adaptivní block-swap spadl do nejhoršího případu (max swap = pomalé).
            if (vramGb > 0) _lastKnownVramGb = vramGb;

            HwInfoLabel = $"{name} · {vramGb:F0} GB VRAM";

            if (vramGb < 6)
            {
                HwSupportsTraining = false;
                HwWarningText      = "Příliš málo VRAM (<6 GB) pro trénink SDXL LoRA. SD 1.5 LoRA může jít, ale je riziko OOM.";
            }
            else if (vramGb < 8)
            {
                HwSupportsTraining = true;
                HwWarningText      = "Hraniční VRAM (6-8 GB). Pro SDXL budu auto-aktivovat gradient checkpointing + batch=1. SD 1.5 v pohodě.";
            }
            else if (vramGb < 12)
            {
                HwSupportsTraining = true;
                HwWarningText      = string.Empty;  // OK, žádné varování
            }
            else
            {
                HwSupportsTraining = true;
                HwWarningText      = string.Empty;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoraTrainingPane: detekce HW selhala");
            HwInfoLabel = "GPU detekce selhala";
        }
    }

}
