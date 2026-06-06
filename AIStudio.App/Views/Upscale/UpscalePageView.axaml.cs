using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using AIStudio.App.ViewModels.Upscale;

namespace AIStudio.App.Views.Upscale;

/// <summary>Code-behind upscale záložky — jen drag&drop obrázků; zbytek je ve VM.</summary>
public partial class UpscalePageView : UserControl
{
    private static readonly System.Collections.Generic.HashSet<string> _dropExtensions =
        new(System.StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

    public UpscalePageView()
    {
        InitializeComponent();
        var drop = this.FindControl<Grid>("DropArea");
        if (drop is not null)
        {
            DragDrop.SetAllowDrop(drop, true);
            drop.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            drop.AddHandler(DragDrop.DropEvent,     OnDrop);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        e.DragEffects = files is not null && files.Any(f =>
            f.TryGetLocalPath() is { } p && _dropExtensions.Contains(Path.GetExtension(p)))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not UpscalePageViewModel vm) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null && _dropExtensions.Contains(Path.GetExtension(p)))
            .Cast<string>();

        vm.AddDroppedImages(paths);
        e.Handled = true;
    }
}
