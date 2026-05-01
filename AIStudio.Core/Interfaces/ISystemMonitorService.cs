using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

public interface ISystemMonitorService
{
    SystemStatus Current { get; }
    event EventHandler<SystemStatus> StatusUpdated;
    Task StartAsync(CancellationToken ct = default);
    void Stop();
}
