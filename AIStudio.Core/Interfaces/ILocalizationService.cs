using AIStudio.Core.Enums;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Poskytuje přeložené řetězce. Implementace volá správný ResX bundle
/// dle aktuálně zvoleného jazyka.
/// </summary>
public interface ILocalizationService
{
    /// <summary>Aktuálně zvolený jazyk.</summary>
    AppLanguage Language { get; set; }

    /// <summary>Vrátí přeložený řetězec. Pokud klíč neexistuje, vrátí klíč samotný.</summary>
    string this[string key] { get; }

    /// <summary>Vyvolá se po každé změně jazyka — UI může rebindovat.</summary>
    event EventHandler? LanguageChanged;
}
