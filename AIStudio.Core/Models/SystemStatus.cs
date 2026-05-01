namespace AIStudio.Core.Models;

public record SystemStatus
{
    public string CpuName { get; init; } = string.Empty;
    public double CpuUsagePercent { get; init; }
    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    public string GpuName { get; init; } = string.Empty;
    public double VramUsedGb { get; init; }
    public double VramTotalGb { get; init; }
    public double GpuUtilizationPercent { get; init; }
    public bool GpuAvailable { get; init; }
    public IReadOnlyList<GpuProcess> GpuProcesses { get; init; } = [];
}

public record GpuProcess
{
    public int Pid { get; init; }
    public string Name { get; init; } = string.Empty;
    public double VramUsedMb { get; init; }
    public bool IsCurrentApp { get; init; }
}
