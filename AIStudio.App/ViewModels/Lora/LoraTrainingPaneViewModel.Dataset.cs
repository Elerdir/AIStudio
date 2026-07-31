using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AIStudio.App.ViewModels.Lora;

/// <summary>
/// Dataset — přidání obrázků z pickeru i z drag&amp;drop, odebrání, vyprázdnění.
/// Partial split z hlavního <see cref="LoraTrainingPaneViewModel"/>.
/// </summary>
public partial class LoraTrainingPaneViewModel
{
    // ── Dataset operace ───────────────────────────────────────────────────────

    /// <summary>
    /// Otevře file picker a přidá vybrané obrázky do <see cref="DatasetItems"/>.
    /// Drag&drop alternativa je v code-behindu LoraLibraryPageView.
    /// </summary>
    [RelayCommand]
    private async Task AddImagesFromPickerAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;

        var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Přidat obrázky do datasetu",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Obrázky") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" } }
            }
        });

        foreach (var f in files)
        {
            var p = f.Path.LocalPath;
            if (!File.Exists(p)) continue;
            AddDatasetImage(p);
        }
    }

    /// <summary>
    /// Přidá obrázek do datasetu — volaná z file pickeru nebo z drag&drop handleru.
    /// Deduplikuje podle absolutní cesty.
    /// </summary>
    public void AddDatasetImage(string imagePath, string? caption = null)
    {
        if (string.IsNullOrEmpty(imagePath)) return;
        if (DatasetItems.Any(i => string.Equals(i.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase)))
            return;

        var item = new LoraDatasetItemViewModel(imagePath, RemoveDatasetItemAsync);
        if (!string.IsNullOrWhiteSpace(caption)) item.Caption = caption!;
        DatasetItems.Add(item);
    }

    private Task RemoveDatasetItemAsync(LoraDatasetItemViewModel item)
    {
        DatasetItems.Remove(item);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ClearDataset()
    {
        DatasetItems.Clear();
    }

}
