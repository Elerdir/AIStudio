using Avalonia.Controls;
using AIStudio.App.ViewModels.Settings;

namespace AIStudio.App.Views.Settings;

public partial class LogViewerWindow : Window
{
    public LogViewerWindow()
    {
        InitializeComponent();
        DataContext = new LogViewerViewModel();
    }
}
