using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIStudio.Core.Enums;
using AIStudio.Core.Interfaces;
using AIStudio.App.ViewModels.Chat;
using AIStudio.App.ViewModels.ImageStudio;
using AIStudio.App.ViewModels.Models;
using AIStudio.App.ViewModels.Settings;
using AIStudio.App.ViewModels.SystemMonitor;
using AIStudio.Infrastructure.Services;

namespace AIStudio.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ISystemMonitorService _monitor;
    private readonly ILlamaService         _llama;

    [ObservableProperty] private ViewModelBase   _currentPage;
    [ObservableProperty] private NavigationPage  _activePage  = NavigationPage.Chat;
    [ObservableProperty] private string          _statusText  = "Připraven";

    [ObservableProperty] private string _vramCompact  = "VRAM —";
    [ObservableProperty] private double _vramPercent;
    [ObservableProperty] private bool   _gpuAvailable;
    [ObservableProperty] private string _gpuNameShort = string.Empty;

    public ChatPageViewModel         ChatPage         { get; }
    public ImageStudioPageViewModel  ImageStudioPage  { get; }
    public ModelManagerPageViewModel ModelManagerPage { get; }
    public SystemPageViewModel       SystemPage       { get; }
    public SettingsPageViewModel     SettingsPage     { get; }

    /// <summary>
    /// Každý child ViewModel dostane ze DI kontejneru jen závislosti, které
    /// reálně potřebuje. MainWindowViewModel jen orchestruje navigaci a
    /// sidebar status — nezná interní závislosti child VM.
    /// </summary>
    public MainWindowViewModel(
        ISystemMonitorService     monitor,
        ILlamaService             llama,
        INavigationService        nav,
        ChatPageViewModel         chatPage,
        ImageStudioPageViewModel  imageStudioPage,
        ModelManagerPageViewModel modelManagerPage,
        SystemPageViewModel       systemPage,
        SettingsPageViewModel     settingsPage)
    {
        _monitor = monitor;
        _llama   = llama;

        ChatPage         = chatPage;
        ImageStudioPage  = imageStudioPage;
        ModelManagerPage = modelManagerPage;
        SystemPage       = systemPage;
        SettingsPage     = settingsPage;

        nav.PageChanged += Navigate;

        _ = ChatPage.InitializeAsync();

        _currentPage = ChatPage;

        _monitor.StatusUpdated += (_, s) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                GpuAvailable = s.GpuAvailable;
                VramPercent  = s.VramTotalGb > 0 ? s.VramUsedGb / s.VramTotalGb * 100.0 : 0;

                VramCompact = s.GpuAvailable
                    ? (s.VramUsedGb > 0
                        ? $"{s.VramUsedGb:F1} / {s.VramTotalGb:F0} GB"
                        : s.VramTotalGb > 0 ? $"{s.VramTotalGb:F0} GB total" : "GPU detekováno")
                    : "GPU —";

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
            NavigationPage.Chat        => ChatPage,
            NavigationPage.ImageStudio => ImageStudioPage,
            NavigationPage.Models      => ModelManagerPage,
            NavigationPage.System      => SystemPage,
            NavigationPage.Settings    => SettingsPage,
            _                          => ChatPage
        };
    }
}
