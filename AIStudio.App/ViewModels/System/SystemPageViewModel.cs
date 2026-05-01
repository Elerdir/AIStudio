using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels.SystemMonitor;

public partial class GpuProcessItem : ViewModelBase
{
    public int Pid { get; init; }
    public string Name { get; init; } = string.Empty;
    public double VramUsedMb { get; init; }
    public bool IsCurrentApp { get; init; }
    public string VramLabel => VramUsedMb >= 1024
        ? $"{VramUsedMb / 1024.0:F1} GB"
        : $"{VramUsedMb:F0} MB";
}

public partial class SystemPageViewModel : ViewModelBase
{
    private readonly ISystemMonitorService _monitor;
    private readonly ILlamaService _llama;

    // CPU
    [ObservableProperty] private string _cpuName = "Zjišťuji…";
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private string _cpuLabel = "—";

    // RAM
    [ObservableProperty] private double _ramUsedGb;
    [ObservableProperty] private double _ramTotalGb;
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private string _ramLabel = "—";

    // GPU
    [ObservableProperty] private string _gpuName = "Zjišťuji...";
    [ObservableProperty] private double _vramUsedGb;
    [ObservableProperty] private double _vramTotalGb;
    [ObservableProperty] private double _vramPercent;
    [ObservableProperty] private string _vramLabel = "—";
    [ObservableProperty] private double _gpuUtilizationPercent;
    [ObservableProperty] private bool _gpuAvailable;

    // GPU toggle
    [ObservableProperty] private bool _useGpu = true;

    // Current model
    [ObservableProperty] private string _loadedModel = "Žádný";
    [ObservableProperty] private bool _isModelLoaded;

    public ObservableCollection<GpuProcessItem> GpuProcesses { get; } = new();

    public SystemPageViewModel(ISystemMonitorService monitor, ILlamaService llama)
    {
        _monitor = monitor;
        _llama = llama;
        _useGpu = llama.UseGpu;
        _monitor.StatusUpdated += OnStatusUpdated;
        UpdateFromStatus(_monitor.Current);
    }

    partial void OnUseGpuChanged(bool value)
    {
        _llama.UseGpu = value;
    }

    private void OnStatusUpdated(object? sender, SystemStatus status)
    {
        // Avalonia UI update z jiného threadu
        Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateFromStatus(status));
    }

    private void UpdateFromStatus(SystemStatus s)
    {
        if (!string.IsNullOrEmpty(s.CpuName))
            CpuName = s.CpuName;
        CpuPercent = s.CpuUsagePercent;
        CpuLabel = $"{s.CpuUsagePercent:F0} %";

        RamUsedGb = s.RamUsedGb;
        RamTotalGb = s.RamTotalGb;
        RamPercent = s.RamTotalGb > 0 ? s.RamUsedGb / s.RamTotalGb * 100.0 : 0;
        RamLabel = $"{s.RamUsedGb:F1} / {s.RamTotalGb:F0} GB";

        GpuName = s.GpuAvailable ? s.GpuName : "GPU nenalezeno";
        GpuAvailable = s.GpuAvailable;
        VramUsedGb = s.VramUsedGb;
        VramTotalGb = s.VramTotalGb;
        VramPercent = s.VramTotalGb > 0 ? s.VramUsedGb / s.VramTotalGb * 100.0 : 0;
        VramLabel = s.GpuAvailable ? $"{s.VramUsedGb:F1} / {s.VramTotalGb:F0} GB" : "—";
        GpuUtilizationPercent = s.GpuUtilizationPercent;

        // Sync GPU processes
        GpuProcesses.Clear();
        foreach (var p in s.GpuProcesses)
            GpuProcesses.Add(new GpuProcessItem
            {
                Pid = p.Pid,
                Name = p.Name,
                VramUsedMb = p.VramUsedMb,
                IsCurrentApp = p.IsCurrentApp
            });

        // LlamaSharp model status
        IsModelLoaded = _llama.IsLoaded;
        LoadedModel = _llama.LoadedModelName ?? "Žádný";
    }

    [RelayCommand]
    private async Task ReleaseVramAsync()
    {
        if (_llama.IsLoaded)
            await _llama.UnloadModelAsync();

        // Vynutit GC + kompaktní LOH
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        IsModelLoaded = _llama.IsLoaded;
        LoadedModel = _llama.LoadedModelName ?? "Žádný";
    }

    [RelayCommand]
    private void KillProcess(GpuProcessItem process)
    {
        if (process.IsCurrentApp) return; // vlastní proces nekilluj
        try
        {
            var p = System.Diagnostics.Process.GetProcessById(process.Pid);
            p.Kill();
        }
        catch { /* proces mohl skončit */ }
    }
}
