namespace AIStudio.App.ViewModels.Chat;

/// <summary>
/// Předdefinovaná role/persona pro chat — kliknutí v UI naplní
/// <see cref="ConversationViewModel.SystemPrompt"/> aktuální konverzace.
/// V první iteraci jsou presety hardcoded (viz <c>ChatPageViewModel.SystemPromptPresets</c>);
/// v navazujícím tasku přijde editor + persistence v SQLite, aby si uživatel
/// mohl přidat vlastní.
/// </summary>
/// <param name="Name">Krátký lidský label pro tlačítko (Asistent / Editor / …).</param>
/// <param name="Prompt">Vlastní obsah systémového promptu, prázdný = bez instrukcí.</param>
public sealed record SystemPromptPreset(string Name, string Prompt);
