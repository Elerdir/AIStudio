using Avalonia.Controls;

namespace AIStudio.App.Views.Models;

/// <summary>
/// Samostatné okno fronty stahování. DataContext se nastavuje zvenčí
/// (instance <c>ModelManagerPageViewModel</c>), takže okno jen vykresluje
/// <c>DownloadQueue</c> a deleguje akce zpět na page VM.
/// </summary>
public partial class DownloadQueueWindow : Window
{
    public DownloadQueueWindow()
    {
        InitializeComponent();
    }
}
