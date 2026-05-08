using AIStudio.Core.Enums;
using AIStudio.Core.Interfaces;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Jednoduchá implementace navigace — singleton bezpečný pro volání
/// z libovolného threadu (event vždy přijde na volajícím threadu).
/// </summary>
public sealed class NavigationService : INavigationService
{
    public event Action<NavigationPage>? PageChanged;

    public void Navigate(NavigationPage page) => PageChanged?.Invoke(page);
}
