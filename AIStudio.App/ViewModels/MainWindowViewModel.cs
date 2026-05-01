using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIStudio.Core.Enums;
using AIStudio.Core.Interfaces;
using AIStudio.App.ViewModels.Chat;
using AIStudio.App.ViewModels.ImageStudio;
using AIStudio.App.ViewModels.Models;
using AIStudio.App.ViewModels.Settings;
using AIStudio.App.ViewModels.SystemMonitor;

namespace AIStudio.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ISystemMonitorService _monitor;
    private readonly ILlamaService _llama;

    [ObservableProperty] private ViewModelBase _currentPage;
    [ObservableProperty] private NavigationPage _activePage = NavigationPage.Chat;
    [ObservableProperty] private string _statusText = "Připraven";

    // Compact VRAM / GPU badge pro sidebar
    [ObservableProperty] private string _vramCompact   = "VRAM —";
    [ObservableProperty] private double _vramPercent;
    [ObservableProperty] private bool   _gpuAvailable;
    [ObservableProperty] private string _gpuNameShort  = string.Empty;

    public ChatPageViewModel ChatPage { get; }
    public ImageStudioPageViewModel ImageStudioPage { get; }
    public ModelManagerPageViewModel ModelManagerPage { get; }
    public SystemPageViewModel SystemPage { get; }
    public SettingsPageViewModel SettingsPage { get; }

    public MainWindowViewModel(ISystemMonitorService monitor, ILlamaService llama,
                               IDownloadService downloader, ISettingsService settings,
                               IChatRepository chatRepo, IComfyService comfy,
                               IImageRepository imageRepo, IComfyInstaller comfyInstaller)
    {
        _monitor = monitor;
        _llama = llama;

        ChatPage = new ChatPageViewModel(llama, chatRepo, settings);
        ChatPage.RequestNavigate = page => Navigate(page);
        _ = ChatPage.InitializeAsync();
        ImageStudioPage = new ImageStudioPageViewModel(comfy, settings, imageRepo);
        ImageStudioPage.RequestNavigate = page => Navigate(page);
        ModelManagerPage = new ModelManagerPageViewModel(downloader, settings, llama);
        SystemPage = new SystemPageViewModel(monitor, llama);
        SettingsPage = new SettingsPageViewModel(settings, comfyInstaller);

        _currentPage = ChatPage;

        _monitor.StatusUpdated += (_, s) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                GpuAvailable = s.GpuAvailable;
                VramPercent  = s.VramTotalGb > 0 ? s.VramUsedGb / s.VramTotalGb * 100.0 : 0;

                // VramUsedGb == 0 = detekováno přes WMI (nelze číst usage) → zobraz jen total
                VramCompact = s.GpuAvailable
                    ? (s.VramUsedGb > 0
                        ? $"{s.VramUsedGb:F1} / {s.VramTotalGb:F0} GB"
                        : s.VramTotalGb > 0 ? $"{s.VramTotalGb:F0} GB total" : "GPU detekováno")
                    : "GPU —";

                // Zkrácený název GPU pro sidebar (max 22 znaků)
                GpuNameShort = s.GpuAvailable && !string.IsNullOrEmpty(s.GpuName)
                    ? (s.GpuName.Length > 22 ? s.GpuName[..22] + "…" : s.GpuName)
                    : string.Empty;

                StatusText = llama.IsLoaded
                    ? $"Model: {llama.LoadedModelName}"
                    : "Připraven";
            });
    }

    [RelayCommand]
    private void Navigate(NavigationPage page)
    {
        ActivePage = page;
        CurrentPage = page switch
        {
            NavigationPage.Chat => ChatPage,
            NavigationPage.ImageStudio => ImageStudioPage,
            NavigationPage.Models => ModelManagerPage,
            NavigationPage.System => SystemPage,
            NavigationPage.Settings => SettingsPage,
            _ => ChatPage
        };
    }
}
