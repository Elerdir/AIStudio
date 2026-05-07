using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

public interface ISettingsService
{
    AppSettings Settings { get; }
    Task LoadAsync();
    Task SaveAsync();

    /// <summary>
    /// Vyvoláno kdykoliv se změní obsah knihovny modelů — stažení, lokální
    /// přidání nebo smazání. Slouží jako notifikace pro Chat / Image Studio
    /// pickery, aby přepočetly seznam dostupných modelů. Vyvolává to
    /// <see cref="NotifyModelLibraryChanged"/>.
    /// </summary>
    event Action? ModelLibraryChanged;

    /// <summary>Volá <see cref="ModelLibraryChanged"/> — bezpečné i když nikdo neodebírá.</summary>
    void NotifyModelLibraryChanged();

    /// <summary>
    /// Vyvoláno po každém úspěšném <see cref="SaveAsync"/>. Slouží pro
    /// reaktivní přepočítávání UI (např. detekce, jestli je ComfyUI cesta
    /// platná po změně v Nastavení).
    /// </summary>
    event Action? SettingsSaved;

    /// <summary>
    /// Vyvoláno po hromadném smazání konverzací z databáze (Settings →
    /// „Smazat všechny chaty"). ChatPageViewModel na to reaguje vyčištěním
    /// své in-memory ObservableCollection.
    /// </summary>
    event Action? ConversationsCleared;

    /// <summary>Volá <see cref="ConversationsCleared"/> — bezpečné i když nikdo neodebírá.</summary>
    void NotifyConversationsCleared();
}
