using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIStudio.Core.Enums;
using AIStudio.Core.Interfaces;
using AIStudio.App.ViewModels.Chat;
using AIStudio.App.ViewModels.Creation;
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
    public CreationPageViewModel     CreationPage     { get; }
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
        CreationPageViewModel     creationPage,
        ModelManagerPageViewModel modelManagerPage,
        LoraLibraryPageViewModel  loraPage,
        SystemPageViewModel       systemPage,
        SettingsPageViewModel     settingsPage,
        UpdateViewModel           updates)
    {
        _monitor = monitor;
        _llama   = llama;

        ChatPage         = chatPage;
        CreationPage     = creationPage;
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
        // Image Studio / Video / Galerie / Upscale (i cílená navigace mezi VM)
        // žijí pod rozcestníkem „Tvorba" — přepneme jeho vnitřní záložku a v sidebaru
        // zvýrazníme jediné tlačítko Tvorba.
        var isCreation = page is NavigationPage.Creation
                              or NavigationPage.ImageStudio
                              or NavigationPage.Video
                              or NavigationPage.Gallery
                              or NavigationPage.Upscale;

        ActivePage = isCreation ? NavigationPage.Creation : page;

        if (isCreation)
        {
            // ShowSubPage si sám obnoví Galerii/Upscale při přepnutí záložky.
            CreationPage.ShowSubPage(page);
            CurrentPage = CreationPage;
            return;
        }

        CurrentPage = page switch
        {
            NavigationPage.Chat     => ChatPage,
            NavigationPage.Models   => ModelManagerPage,
            NavigationPage.Lora     => LoraPage,
            NavigationPage.System   => SystemPage,
            NavigationPage.Settings => SettingsPage,
            _                       => ChatPage
        };
    }
}
