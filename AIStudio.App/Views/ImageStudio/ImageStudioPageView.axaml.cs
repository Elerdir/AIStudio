using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using AIStudio.App.ViewModels.ImageStudio;

namespace AIStudio.App.Views.ImageStudio;

public partial class ImageStudioPageView : UserControl
{
    public ImageStudioPageView()
    {
        InitializeComponent();
    }

    private void OnReferenceImageDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() != null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnReferenceImageDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        var vm = GetActiveGenerator();
        if (vm is null) return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null)
            .Cast<string>();

        vm.AddReferenceImages(paths);
        e.Handled = true;
    }

    private ImageGeneratorViewModel? GetActiveGenerator()
        => (DataContext as ImageStudioPageViewModel)?.ActiveGenerator;
}
