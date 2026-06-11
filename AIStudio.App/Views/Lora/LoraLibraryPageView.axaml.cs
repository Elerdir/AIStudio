using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Serilog;
using AIStudio.App.ViewModels.Lora;

namespace AIStudio.App.Views.Lora;

/// <summary>
/// Code-behind LoRA stránky. Hlavní logika je ve VM; tady řešíme pouze
/// drag&amp;drop fotek do dataset zóny, který se přes čistý MVVM dělá špatně
/// (DataTransfer.TryGetFiles vyžaduje přístup k DataTransfer objektu z handleru).
/// </summary>
public partial class LoraLibraryPageView : UserControl
{
    public LoraLibraryPageView()
    {
        InitializeComponent();

        // Drag&drop pro dataset zónu (jak prázdná empty-state border, tak items panel)
        // Najdeme oba elementy jen pokud existují — během prvního inflate ještě
        // nemusí být v tree (UserControl ještě nemá data context vázaný).
        AttachedToVisualTree += (_, _) =>
        {
            WireDatasetDropHandlers();
            WireVideoDropHandlers();
        };
    }

    private void WireDatasetDropHandlers()
    {
        foreach (var name in new[] { "DatasetDropZone", "DatasetItemsPanel" })
        {
            var ctrl = this.FindControl<Control>(name);
            if (ctrl is null) continue;

            DragDrop.SetAllowDrop(ctrl, true);
            ctrl.AddHandler(DragDrop.DragOverEvent, OnDatasetDragOver);
            ctrl.AddHandler(DragDrop.DropEvent,     OnDatasetDrop);
        }
    }

    // ── Drag&drop videí (záložka „Z videa") ───────────────────────────────────
    private static readonly string[] VideoExts = { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v" };

    private void WireVideoDropHandlers()
    {
        var ctrl = this.FindControl<Control>("VideoDropZone");
        if (ctrl is null) return;

        DragDrop.SetAllowDrop(ctrl, true);
        ctrl.AddHandler(DragDrop.DragOverEvent, OnVideoDragOver);
        ctrl.AddHandler(DragDrop.DropEvent,     OnVideoDrop);
    }

    private void OnVideoDragOver(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        e.DragEffects = files is not null && files.Any(f =>
            f.Path?.LocalPath is { } p && VideoExts.Contains(Path.GetExtension(p).ToLowerInvariant()))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnVideoDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not LoraLibraryPageViewModel page || page.VideoLoraPane is null) return;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        foreach (var f in files)
        {
            try
            {
                var local = f.Path?.LocalPath;
                if (string.IsNullOrEmpty(local) || !File.Exists(local)) continue;
                if (!VideoExts.Contains(Path.GetExtension(local).ToLowerInvariant())) continue;
                page.VideoLoraPane.AddVideo(local);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "VideoDrop: položka selhala");
            }
        }
        e.Handled = true;
    }

    private void OnDatasetDragOver(object? sender, DragEventArgs e)
    {
        // Accept jen pokud jde o soubory (obrázky budou validovány v Drop handleru)
        var files = e.DataTransfer.TryGetFiles();
        e.DragEffects = files is not null && files.Any()
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDatasetDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not LoraLibraryPageViewModel page || page.TrainingPane is null) return;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        var allowed = new[] { ".png", ".jpg", ".jpeg", ".webp" };

        foreach (var f in files)
        {
            try
            {
                var local = f.Path?.LocalPath;
                if (string.IsNullOrEmpty(local) || !File.Exists(local)) continue;

                var ext = Path.GetExtension(local).ToLowerInvariant();
                if (!allowed.Contains(ext))
                {
                    Log.Information("LoraDatasetDrop: ignoruji {Path} (nepodporovaná přípona {Ext})", local, ext);
                    continue;
                }

                page.TrainingPane.AddDatasetImage(local);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "LoraDatasetDrop: položka selhala");
            }
        }

        e.Handled = true;
    }
}
