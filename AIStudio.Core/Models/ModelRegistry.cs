namespace AIStudio.Core.Models;

/// <summary>
/// Centrální katalog modelů — jediné místo, kde jsou definovány jména modelů
/// a jejich mapování na fyzické soubory. Přidání nového modelu = jeden záznam zde.
/// </summary>
public sealed record ModelDefinition(
    string DisplayName,
    string FileName,
    ModelKind Kind);

public enum ModelKind { Chat, Image }

public static class ModelRegistry
{
    private static readonly IReadOnlyList<ModelDefinition> All = new[]
    {
        // LLM chat modely
        new ModelDefinition("Llama 3.1 8B Instruct Q4_K_M",  "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf",  ModelKind.Chat),
        new ModelDefinition("Mistral 7B Instruct v0.3 Q4_K_M","Mistral-7B-Instruct-v0.3-Q4_K_M.gguf",   ModelKind.Chat),
        new ModelDefinition("Llama 3.3 70B Instruct Q4_K_M", "Llama-3.3-70B-Instruct-Q4_K_M.gguf",      ModelKind.Chat),
        new ModelDefinition("Gemma 3 27B Instruct Q4_K_M",   "gemma-3-27b-it-Q4_K_M.gguf",              ModelKind.Chat),
        new ModelDefinition("Qwen 2.5 14B Instruct Q4_K_M",  "Qwen2.5-14B-Instruct-Q4_K_M.gguf",        ModelKind.Chat),
        new ModelDefinition("Phi-4 Q4_K_M",                  "phi-4-Q4_K_M.gguf",                        ModelKind.Chat),
        // Qwen3
        new ModelDefinition("Qwen3 8B Q4_K_M",               "Qwen3-8B-Q4_K_M.gguf",                    ModelKind.Chat),
        new ModelDefinition("Qwen3 14B Q4_K_M",              "Qwen3-14B-Q4_K_M.gguf",                   ModelKind.Chat),
        new ModelDefinition("Qwen3 32B Q4_K_M",              "Qwen3-32B-Q4_K_M.gguf",                   ModelKind.Chat),
        new ModelDefinition("Qwen3 30B-A3B Q4_K_M",          "Qwen3-30B-A3B-Q4_K_M.gguf",              ModelKind.Chat),
        // Kreativní psaní
        new ModelDefinition("Mistral Nemo Instruct 2407 Q4_K_M",         "Mistral-Nemo-Instruct-2407-Q4_K_M.gguf",         ModelKind.Chat),
        new ModelDefinition("Magnum v4 22B Q4_K_M",                      "magnum-v4-22b-Q4_K_M.gguf",                      ModelKind.Chat),
        new ModelDefinition("Cydonia 22B v1 Q4_K_M",                     "Cydonia-22B-v1-Q4_K_M.gguf",                     ModelKind.Chat),
        new ModelDefinition("Lumimaid v0.2 12B Q4_K_M",                  "Lumimaid-v0.2-12B-Q4_K_M.gguf",                  ModelKind.Chat),
        new ModelDefinition("L3 Stheno v3.2 8B Q4_K_M",                  "L3-8B-Stheno-v3.2-Q4_K_M.gguf",                 ModelKind.Chat),
        new ModelDefinition("Llama 3.3 70B Instruct abliterated Q4_K_M", "Llama-3.3-70B-Instruct-abliterated-Q4_K_M.gguf", ModelKind.Chat),
    };

    /// <summary>Všechny registrované chat modely.</summary>
    public static IReadOnlyList<ModelDefinition> ChatModels =>
        All.Where(m => m.Kind == ModelKind.Chat).ToArray();

    /// <summary>
    /// Vrátí filename pro daný display name, nebo null pokud není v katalogu.
    /// Case-insensitive lookup.
    /// </summary>
    public static string? GetFileName(string displayName) =>
        All.FirstOrDefault(m => string.Equals(m.DisplayName, displayName,
            StringComparison.OrdinalIgnoreCase))?.FileName;

    /// <summary>Vrátí dictionary displayName → fileName pro zpětnou kompatibilitu.</summary>
    public static IReadOnlyDictionary<string, string> AsFileNameDictionary() =>
        All.ToDictionary(m => m.DisplayName, m => m.FileName,
            StringComparer.OrdinalIgnoreCase);
}
