using AIStudio.Core.Enums;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Navigační abstrakce — umožňuje child ViewModelům iniciovat přechod na stránku
/// bez přímé závislosti na MainWindowViewModel nebo callbacku.
/// </summary>
public interface INavigationService
{
    /// <summary>Naviguj na zadanou stránku.</summary>
    void Navigate(NavigationPage page);

    /// <summary>Vyvolán po každé navigaci — MainWindowViewModel se přihlásí k odběru.</summary>
    event Action<NavigationPage>? PageChanged;
}
