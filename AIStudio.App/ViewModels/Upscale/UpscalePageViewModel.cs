using System.Collections.ObjectModel;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Services;

namespace AIStudio.App.ViewModels.Upscale;

/// <summary>
/// Záložka „Upscale" — dodej obrázek (ze sledované složky nebo přetažením) a jen ho zvětši
/// existujícím ESRGAN upscalem (<see cref="IChatImageOrchestrator.UpscaleImageAsync"/>, jede
/// přes ComfyUI). Výstup se ukládá do galerie. Samostatná galerie nad konkrétní složkou —
/// cesta se nastavuje v Nastavení nebo tlačítkem zde.
/// </summary>
public partial class UpscalePageViewModel : ViewModelBase
{
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

    private readonly ISettingsService        _settings;
    private readonly IComfyService           _comfy;
    private readonly IChatImageOrchestrator? _orch;

    public UpscalePageViewModel(ISettingsService settings, IComfyService comfy,
                                IChatImageOrchestrator? orch = null)
    {
        _settings = settings;
        _comfy    = comfy;
        _orch     = orch;
        _sourceDirectory = ResolveSourceDir();
    }

    public ObservableCollection<UpscaleItemViewModel> Images { get; } = new();

    public bool HasImages => Images.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourceDir))]
    private string _sourceDirectory = string.Empty;

    public bool HasSourceDir => !string.IsNullOrWhiteSpace(SourceDirectory) && Directory.Exists(SourceDirectory);

    [ObservableProperty] private bool   _isScanning;
    [ObservableProperty] private string _statusLine = string.Empty;

    private string ResolveSourceDir()
    {
        var custom = _settings.Settings.UpscaleSourceDirectory;
        return string.IsNullOrWhiteSpace(custom) ? AppPaths.DefaultImagesDirectory : custom;
    }

    public override Task InitializeAsync() => RefreshAsync();

    /// <summary>Naskenuje sledovanou složku na obrázky a naplní galerii.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var dir = SourceDirectory;
        IsScanning = true;
        try
        {
            var files = await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    return new List<string>();
                try
                {
                    return Directory.EnumerateFiles(dir)
                        .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())
                                    && !f.EndsWith(".thumb.png", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                        .ToList();
                }
                catch (Exception ex) { Log.Warning(ex, "Upscale: sken {Dir} selhal", dir); return new List<string>(); }
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Images.Clear();
                foreach (var f in files)
                {
                    var item = new UpscaleItemViewModel { FilePath = f };
                    item.EnsureThumbnail();
                    Images.Add(item);
                }
                OnPropertyChanged(nameof(HasImages));
                StatusLine = files.Count == 0
                    ? "Ve složce nejsou žádné obrázky. Vyber jinou složku nebo přetáhni obrázek sem."
                    : $"{files.Count} obrázků";
            });
        }
        finally { IsScanning = false; }
    }

    /// <summary>Zvětší jednu položku přes ESRGAN (uloží do galerie). Vyžaduje běžící ComfyUI.</summary>
    [RelayCommand]
    private async Task UpscaleAsync(UpscaleItemViewModel? item)
    {
        if (item is null || item.IsUpscaling) return;
        if (_orch is null) { item.Status = "Upscale služba není dostupná."; return; }
        if (!_comfy.IsRunning) { item.Status = "ComfyUI není spuštěno"; return; }
        if (!File.Exists(item.FilePath)) { item.Status = "Soubor už neexistuje."; return; }

        item.IsUpscaling = true;
        item.IsDone      = false;
        item.Status      = "Zvětšuji…";
        var progress = new Progress<int>(p => Dispatcher.UIThread.Post(() => item.Status = $"Zvětšuji {p} %"));
        try
        {
            var result = await _orch.UpscaleImageAsync(item.FilePath, progress, CancellationToken.None);
            if (result.Success)
            {
                item.IsDone = true;
                item.Status = "Hotovo — uloženo do galerie";
                Log.Information("Upscale: hotovo {File} → {Out}", item.FileName, result.ImagePath);
            }
            else
            {
                item.Status = result.ErrorMessage ?? "Zvětšení selhalo.";
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Upscale: položka {File} selhala", item.FileName);
            item.Status = "Chyba: " + ex.Message;
        }
        finally { item.IsUpscaling = false; }
    }

    /// <summary>Vybere sledovanou složku (uloží do nastavení) a naskenuje ji.</summary>
    [RelayCommand]
    private async Task PickFolderAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;

        IStorageFolder? start = HasSourceDir
            ? await win.StorageProvider.TryGetFolderFromPathAsync(SourceDirectory) : null;

        var folders = await win.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Vyber složku s obrázky k upscalu",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        if (folders.Count == 0) return;
        SourceDirectory = folders[0].Path.LocalPath;
        _settings.Settings.UpscaleSourceDirectory = SourceDirectory;
        try { await _settings.SaveAsync(); } catch (Exception ex) { Log.Warning(ex, "Upscale: uložení složky selhalo"); }
        await RefreshAsync();
    }

    /// <summary>Přidá přetažené obrázky jako položky (upscalují se na místě, nemusí být ve složce).</summary>
    public void AddDroppedImages(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue;
            if (!ImageExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())) continue;
            if (Images.Any(i => string.Equals(i.FilePath, p, StringComparison.OrdinalIgnoreCase))) continue;

            var item = new UpscaleItemViewModel { FilePath = p };
            item.EnsureThumbnail();
            Images.Insert(0, item);
            added++;
        }
        if (added > 0)
        {
            OnPropertyChanged(nameof(HasImages));
            StatusLine = $"Přidáno {added} (přetažením)";
        }
    }

    [RelayCommand]
    private void OpenSourceFolder()
    {
        if (!HasSourceDir) return;
        try { AIStudio.Infrastructure.Services.PlatformShell.Open(SourceDirectory); }
        catch (Exception ex) { Log.Warning(ex, "Upscale: otevření složky selhalo"); }
    }
}
