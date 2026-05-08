using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels;

/// <summary>
/// Řídí UI pro kontrolu aktualizací a jejich instalaci.
/// Singleton — MainWindowViewModel ho drží a exposes jako property.
/// </summary>
public partial class UpdateViewModel : ViewModelBase
{
    private readonly IUpdateService _updater;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    private UpdateInfo? _availableUpdate;

    [ObservableProperty] private bool   _isChecking;
    [ObservableProperty] private bool   _isInstalling;
    [ObservableProperty] private int    _installProgress;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public bool HasUpdate => AvailableUpdate is not null;

    public string UpdateLabel => AvailableUpdate is { } u
        ? $"Aktualizace {u.Version}"
        : string.Empty;

    public UpdateViewModel(IUpdateService updater)
    {
        _updater = updater;
    }

    [RelayCommand]
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (IsChecking) return;
        IsChecking    = true;
        StatusMessage = "Kontroluji aktualizace…";

        try
        {
            var info = await _updater.CheckForUpdateAsync(ct);
            AvailableUpdate = info;
            StatusMessage   = info is null
                ? $"Verze {_updater.CurrentVersion} je aktuální."
                : $"Dostupná aktualizace: {info.Version}";
            OnPropertyChanged(nameof(UpdateLabel));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UpdateViewModel: chyba kontroly");
            StatusMessage = "Kontrola selhala.";
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    public async Task InstallAsync(CancellationToken ct = default)
    {
        if (AvailableUpdate is null || IsInstalling) return;
        IsInstalling     = true;
        InstallProgress  = 0;
        StatusMessage    = "Stahuji installer…";

        var progress = new Progress<DownloadProgressInfo>(p =>
        {
            InstallProgress = p.Total > 0 ? (int)(p.Downloaded * 100 / p.Total) : 0;
            StatusMessage   = $"Stahuji… {InstallProgress} %";
        });

        try
        {
            await _updater.DownloadAndInstallAsync(AvailableUpdate, progress, ct);
            // Sem se nedostaneme — installer zavře aplikaci
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UpdateViewModel: instalace selhala");
            StatusMessage = $"Instalace selhala: {ex.Message}";
            IsInstalling  = false;
        }
    }
}
