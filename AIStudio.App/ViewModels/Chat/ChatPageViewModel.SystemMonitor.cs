using CommunityToolkit.Mvvm.ComponentModel;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels.Chat;

/// <summary>
/// Live RAM/VRAM ticker v chat headeru. Partial split z ChatPageViewModel.cs —
/// system monitor binding je self-contained area (jen subscribe z konstruktoru
/// + UI thread dispatch), ideální kandidát na izolaci.
/// </summary>
public partial class ChatPageViewModel
{
    // ── Live system metrics (RAM / VRAM ticker v chat headeru) ───────────────

    /// <summary>Využití RAM v GB (z <see cref="Core.Interfaces.ISystemMonitorService"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryUsageLabel))]
    private double _ramUsedGb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryUsageLabel))]
    private double _ramTotalGb;

    /// <summary>Využití VRAM v GB (jen pro GPU backend — pro CPU zůstává 0).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryUsageLabel))]
    private double _vramUsedGb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryUsageLabel))]
    private double _vramTotalGb;

    /// <summary>
    /// Lidsky čitelný popisek paměti, který se ukáže v badge vedle modelu.
    /// Pro GPU backend ukazuje VRAM (kolik model+aplikace zabírají v kartě),
    /// pro CPU backend ukazuje RAM. Při total=0 (monitor ještě neměřil)
    /// vrátí prázdný řetězec, badge se v UI skryje.
    /// </summary>
    public string MemoryUsageLabel
    {
        get
        {
            if (IsGpuBackend && VramTotalGb > 0)
                return $"VRAM {VramUsedGb:F1} / {VramTotalGb:F1} GB";
            if (!IsGpuBackend && RamTotalGb > 0)
                return $"RAM {RamUsedGb:F1} / {RamTotalGb:F1} GB";
            return string.Empty;
        }
    }

    /// <summary>Tooltip s detailem na hover — vysvětlí co se měří.</summary>
    public string MemoryUsageTooltip => IsGpuBackend
        ? "Využití VRAM celou aplikací (model + ostatní procesy). Aktualizováno každé 2,5 s."
        : "Využití operační paměti (RAM) celou aplikací. Aktualizováno každé 2,5 s.";

    /// <summary>True pokud je model načtený, ale jede přes CPU (ne GPU).</summary>
    public bool IsCpuBackend => IsModelLoaded && !IsGpuBackend;

    /// <summary>Tooltip s detailem backendu.</summary>
    public string BackendTooltip => IsGpuBackend
        ? "Model běží na GPU (CUDA) — rychlá inference"
        : "Model běží na CPU / RAM — inference může být pomalejší";

    private void OnSystemStatusUpdated(object? sender, SystemStatus status)
    {
        // StatusUpdated jezdí z thread-pool threadu monitorovacího loopu;
        // UI thread switch obstará Dispatcher.UIThread.Post.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplySystemStatus(status));
    }

    private void ApplySystemStatus(SystemStatus s)
    {
        RamUsedGb   = s.RamUsedGb;
        RamTotalGb  = s.RamTotalGb;
        VramUsedGb  = s.VramUsedGb;
        VramTotalGb = s.VramTotalGb;
    }
}
