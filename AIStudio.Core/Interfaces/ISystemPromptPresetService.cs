using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Spravuje seznam systémových promptů — kombinuje vestavěné role
/// (Asistent / Editor / Kreativní psaní / Programátor / Brainstorm /
/// Bez instrukcí) s uživatelskými, které si uživatel může uložit
/// přes Nastavení.
///
/// Builtin presety jsou immutable a vždy v seznamu. Uživatelské jsou
/// perzistované do JSON souboru v <c>%AppData%/AIStudio/prompt-presets.json</c>.
/// </summary>
public interface ISystemPromptPresetService
{
    /// <summary>Vestavěné presety — hard-coded, bez I/O.</summary>
    IReadOnlyList<SystemPromptPreset> BuiltInPresets { get; }

    /// <summary>
    /// Vrátí buildin + uživatelské presety. Při prvním volání načte
    /// uživatelské ze souboru. Pokud soubor neexistuje, vrací pouze buildin.
    /// </summary>
    Task<IReadOnlyList<SystemPromptPreset>> LoadAllAsync();

    /// <summary>
    /// Uloží uživatelský preset. Pokud preset stejného jména už existuje,
    /// přepíše ho. Builtin presety nelze přepisovat (vyhodí <see cref="InvalidOperationException"/>).
    /// </summary>
    Task SaveCustomAsync(SystemPromptPreset preset);

    /// <summary>
    /// Smaže uživatelský preset podle jména. Builtin presety nelze mazat
    /// (vrátí false). Vrátí true pokud byl preset nalezen a smazán.
    /// </summary>
    Task<bool> DeleteCustomAsync(string name);
}
