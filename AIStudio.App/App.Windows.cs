using Microsoft.Extensions.DependencyInjection;
using AIStudio.Core.Interfaces;
using AIStudio.Infrastructure.Services;

namespace AIStudio.App;

/// <summary>
/// Windows-only část DI bootstrapu. Tento soubor je na non-Windows
/// <c>Compile Remove</c>nutý (viz AIStudio.App.csproj), takže reference na
/// Windows-only implementace (WMI / nvidia-smi / 7z native) se na macOS / Linux
/// vůbec nekompilují. Na non-Windows zůstane <c>RegisterWindowsPlatformServices</c>
/// jako partial bez těla (no-op) — viz deklarace v App.axaml.cs.
/// </summary>
public partial class App
{
    partial void RegisterWindowsPlatformServices(IServiceCollection services)
    {
        services.AddSingleton<IGpuDetector, WindowsGpuDetector>();
        services.AddSingleton<ISystemMonitorService, WindowsSystemMonitorService>();
        services.AddSingleton<IComfyInstaller, WindowsComfyInstaller>();
    }
}
