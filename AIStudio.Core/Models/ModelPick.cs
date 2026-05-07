namespace AIStudio.Core.Models;

/// <summary>
/// Provider, ze kterého discovery service tahá pro daný Pick. Každý Pick míří
/// jen do jednoho zdroje — nemíchat výsledky napříč v rámci jedné sekce, je
/// to matoucí (jiné rating systémy, jiné typy souborů).
/// </summary>
public enum PickProvider { HuggingFace, Civitai }

/// <summary>
/// Vyšší kategorie pro tab/sekci v UI. Chat = LLM modely (GGUF), Image = SD/SDXL/FLUX
/// checkpointy, Lora = LoRA pro image modely.
/// </summary>
public enum PickKind { Chat, Image, Lora }

/// <summary>
/// Jeden „doporučený řez" — recept, jak z online API vytáhnout aktuální top-N
/// modelů spadajících do dané sekce. UI vykreslí každý Pick jako akordion s
/// nadpisem (<see cref="Title"/>), popiskem (<see cref="Hint"/>) a seznamem
/// reálně existujících modelů z odpovědi API.
///
/// Definice jsou statické (v <c>ModelDiscoveryService</c>), ale výsledky jsou
/// vždy živé — když autor smaže model, prostě se příště v sekci neobjeví.
/// </summary>
public sealed record ModelPick(
    string       Id,             // stabilní identifikátor pro cache klíč
    string       Title,          // nadpis sekce ("Chat – obecné 7B/8B")
    string       Hint,           // krátké vysvětlení, na co se hodí
    PickKind     Kind,
    PickProvider Provider,
    /// <summary>HF: search query nebo prázdné pro top. Civitai: full-text query.</summary>
    string?      Query     = null,
    /// <summary>HF tag filter (např. <c>"gguf"</c>); pro Civitai null.</summary>
    string?      HfFilter  = null,
    /// <summary>HF pipeline_tag (např. <c>"text-generation"</c>); pro Civitai null.</summary>
    string?      HfTask    = null,
    /// <summary>Civitai type (Checkpoint/LORA/…); pro HF null.</summary>
    string?      CivitaiType = null,
    /// <summary>Civitai base model (<c>"SDXL 1.0"</c>, <c>"SD 1.5"</c>, <c>"Pony"</c>, <c>"Flux.1 D"</c>); volitelné.</summary>
    string?      CivitaiBaseModel = null,
    int          Limit     = 8);
