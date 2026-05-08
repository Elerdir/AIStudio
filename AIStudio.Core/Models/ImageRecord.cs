namespace AIStudio.Core.Models;

/// <summary>
/// Čistý POCO record pro uložení/načtení záznamu o vygenerovaném obrázku z databáze.
/// </summary>
public record ImageRecord(
    string   Id,
    string   FilePath,
    string   Prompt,
    string   ModelName,
    long     Seed,
    int      Width,
    int      Height,
    int      Steps,
    double   Cfg,
    string   Sampler,
    string   Scheduler,
    DateTime GeneratedAt);
