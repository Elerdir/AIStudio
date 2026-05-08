using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using AIStudio.App.ViewModels.ImageStudio;
using System.Collections.Generic;

namespace AIStudio.App.Views.ImageStudio;

public partial class ImageStudioPageView : UserControl
{
    public ImageStudioPageView()
    {
        InitializeComponent();

        // Drag & drop pro referenční obrázky — oba módy (Smart i Manuální)
        SmartRefArea.AddHandler(DragDrop.DragOverEvent, OnReferenceImageDragOver);
        SmartRefArea.AddHandler(DragDrop.DropEvent,     OnReferenceImageDrop);
        ManualRefArea.AddHandler(DragDrop.DragOverEvent, OnReferenceImageDragOver);
        ManualRefArea.AddHandler(DragDrop.DropEvent,     OnReferenceImageDrop);
    }

    private static readonly HashSet<string> _imageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

    private void OnReferenceImageDragOver(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        e.DragEffects = files is not null && files.Any(f =>
            f.TryGetLocalPath() is { } p && _imageExtensions.Contains(Path.GetExtension(p)))
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
            .Where(p => p is not null && _imageExtensions.Contains(Path.GetExtension(p)))
            .Cast<string>();

        vm.AddReferenceImages(paths);
        e.Handled = true;
    }

    private ImageGeneratorViewModel? GetActiveGenerator()
        => (DataContext as ImageStudioPageViewModel)?.ActiveGenerator;
}
