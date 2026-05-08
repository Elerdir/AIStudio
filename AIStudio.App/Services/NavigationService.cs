using AIStudio.Core.Enums;
using AIStudio.Core.Interfaces;

namespace AIStudio.App.Services;

/// <summary>
/// Jednoduchá implementace navigace — singleton, singleton bezpečný pro volání
/// z libovolného threadu (event vždy přijde na volajícím threadu, MainWindowViewModel
/// si ho sám přeposílá na UI thread).
/// </summary>
public sealed class NavigationService : INavigationService
{
    public event Action<NavigationPage>? PageChanged;

    public void Navigate(NavigationPage page) => PageChanged?.Invoke(page);
}
