using CommunityToolkit.Mvvm.ComponentModel;
using AIStudio.Core.Enums;
using AIStudio.App.ViewModels.Gallery;
using AIStudio.App.ViewModels.ImageStudio;
using AIStudio.App.ViewModels.Upscale;
using AIStudio.App.ViewModels.Video;

namespace AIStudio.App.ViewModels.Creation;

/// <summary>
/// Rozcestník „Tvorba" — sjednocuje čtyři dříve samostatné stránky (Image Studio,
/// Video, Galerie, Upscale) pod jedno tlačítko v sidebaru. Uvnitř je <c>TabControl</c>
/// se záložkami; tento VM jen drží podřízené VM a aktuální index záložky.
///
/// <para>Podřízené VM zůstávají DI singletony (a navigace mezi nimi přes
/// <see cref="Core.Interfaces.INavigationService"/> funguje jako dřív) — sem se jen
/// vystaví a směruje se na ně vnitřní záložkou.</para>
/// </summary>
public partial class CreationPageViewModel : ViewModelBase
{
    public ImageStudioPageViewModel ImageStudioPage { get; }
    public VideoPageViewModel       VideoPage       { get; }
    public GalleryPageViewModel     GalleryPage     { get; }
    public UpscalePageViewModel     UpscalePage     { get; }

    /// <summary>Index aktivní vnitřní záložky: 0=Obrázky, 1=Videa, 2=Galerie, 3=Upscale.</summary>
    [ObservableProperty] private int _selectedTabIndex;

    public CreationPageViewModel(
        ImageStudioPageViewModel imageStudioPage,
        VideoPageViewModel       videoPage,
        GalleryPageViewModel     galleryPage,
        UpscalePageViewModel     upscalePage)
    {
        ImageStudioPage = imageStudioPage;
        VideoPage       = videoPage;
        GalleryPage     = galleryPage;
        UpscalePage     = upscalePage;
    }

    /// <summary>
    /// Přepne na vnitřní záložku odpovídající dané stránce. Volá se z
    /// <c>MainWindowViewModel.Navigate</c> jak pro tlačítko „Tvorba"
    /// (<see cref="NavigationPage.Creation"/> → ponechá aktuální záložku), tak pro
    /// cílenou navigaci mezi VM (Galerie → Image Studio apod.).
    /// </summary>
    public void ShowSubPage(NavigationPage page)
    {
        var idx = page switch
        {
            NavigationPage.ImageStudio => 0,
            NavigationPage.Video       => 1,
            NavigationPage.Gallery     => 2,
            NavigationPage.Upscale     => 3,
            _                          => SelectedTabIndex   // Creation → zachovej poslední
        };

        // Když se index nemění, setter nefíruje OnChanged → vynuť refresh ručně.
        if (idx == SelectedTabIndex) RefreshFor(idx);
        else                         SelectedTabIndex = idx;
    }

    partial void OnSelectedTabIndexChanged(int value) => RefreshFor(value);

    /// <summary>Galerie i Upscale čtou obsah složky/DB při otevření — obnov je.</summary>
    private void RefreshFor(int index)
    {
        switch (index)
        {
            case 2: _ = GalleryPage.RefreshAsync(); break;
            case 3: _ = UpscalePage.RefreshAsync(); break;
        }
    }
}
