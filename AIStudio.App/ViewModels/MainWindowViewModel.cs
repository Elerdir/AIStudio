using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIStudio.Core.Enums;
using AIStudio.Core.Interfaces;
using AIStudio.App.ViewModels.Chat;
using AIStudio.App.ViewModels.Gallery;
using AIStudio.App.ViewModels.ImageStudio;
using AIStudio.App.ViewModels.Lora;
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
    public Video.VideoPageViewModel  VideoPage        { get; }
    public GalleryPageViewModel      GalleryPage      { get; }
    public ModelManagerPageViewModel ModelManagerPage { get; }
    public LoraLibraryPageViewModel  LoraPage         { get; }
    public SystemPageViewModel       SystemPage       { get; }
    public SettingsPageViewModel     SettingsPage     { get; }
    public UpdateViewModel           Updates          { get; }

    public MainWindowViewModel(
        ISystemMonitorService     monitor,
        ILlamaService             llama,
        INavigationService        nav,
        ChatPageViewModel         chatPage,
        ImageStudioPageViewModel  imageStudioPage,
        Video.VideoPageViewModel  videoPage,
        GalleryPageViewModel      galleryPage,
        ModelManagerPageViewModel modelManagerPage,
        LoraLibraryPageViewModel  loraPage,
        SystemPageViewModel       systemPage,
        SettingsPageViewModel     settingsPage,
        UpdateViewModel           updates)
    {
        _monitor = monitor;
        _llama   = llama;

        ChatPage         = chatPage;
        ImageStudioPage  = imageStudioPage;
        VideoPage        = videoPage;
        GalleryPage      = galleryPage;
        ModelManagerPage = modelManagerPage;
        LoraPage         = loraPage;
        SystemPage       = systemPage;
        SettingsPage     = settingsPage;
        Updates          = updates;

        nav.PageChanged += Navigate;

        _ = ChatPage.InitializeAsync();

        // Kontrola aktualizací na pozadí — netblokuje start, tiše selže
        _ = Task.Run(async () =>
        {
            await Task.Delay(8_000);   // počkej 8 s než se app usadí
            await Updates.CheckAsync();
        });

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
            NavigationPage.Video       => VideoPage,
            NavigationPage.Gallery     => GalleryPage,
            NavigationPage.Models      => ModelManagerPage,
            NavigationPage.Lora        => LoraPage,
            NavigationPage.System      => SystemPage,
            NavigationPage.Settings    => SettingsPage,
            _                          => ChatPage
        };

        // Galerie čte z DB stránkovaně — při každém otevření obnov (mohlo přibýt
        // z chatu / Image Studia / upscale).
        if (page == NavigationPage.Gallery)
            _ = GalleryPage.RefreshAsync();
    }
}
