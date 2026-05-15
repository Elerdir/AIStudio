namespace AIStudio.Core.Models;

/// <summary>
/// Předdefinovaná role/persona pro chat — kliknutí v UI naplní systémový prompt
/// aktuální konverzace.
///
/// Service <see cref="Interfaces.ISystemPromptPresetService"/> kombinuje
/// hard-coded builtin presety (5 rolí + „Bez instrukcí") s uživatelskými,
/// které se ukládají do <c>%AppData%/AIStudio/prompt-presets.json</c>.
/// </summary>
/// <param name="Name">Krátký lidský label pro tlačítko (Asistent / Editor / …).</param>
/// <param name="Prompt">Vlastní obsah systémového promptu, prázdný = bez instrukcí.</param>
/// <param name="IsBuiltIn">True pro buildin presety — UI je nezobrazí v editoru pro úpravu.</param>
public sealed record SystemPromptPreset(string Name, string Prompt, bool IsBuiltIn = false);
