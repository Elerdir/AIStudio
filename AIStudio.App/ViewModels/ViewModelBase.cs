using CommunityToolkit.Mvvm.ComponentModel;

namespace AIStudio.App.ViewModels;

/// <summary>
/// Základní třída pro všechny ViewModely. Poskytuje:
/// - ObservableObject (CommunityToolkit.Mvvm) pro [ObservableProperty] a [RelayCommand]
/// - IAsyncDisposable pro uvolnění zdrojů (modely, connections, subscriptions)
/// - Virtuální InitializeAsync() pro asynchronní inicializaci po konstruktoru
/// </summary>
public abstract class ViewModelBase : ObservableObject, IAsyncDisposable
{
    private bool _disposed;

    /// <summary>
    /// Asynchronní inicializace — volána po DI sestavení. Výchozí implementace
    /// je no-op; přepiš v podtřídě pro načítání dat z DB, API atd.
    /// </summary>
    public virtual Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Místo pro uvolnění zdrojů specifických pro podtřídu. Voláno jednou z DisposeAsync().
    /// </summary>
    protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await DisposeAsyncCore();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
