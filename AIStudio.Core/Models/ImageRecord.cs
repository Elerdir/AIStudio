namespace AIStudio.Core.Models;

/// <summary>
/// Čistý POCO record pro uložení/načtení záznamu o vygenerovaném médiu (obrázku či videu)
/// z databáze. <see cref="MediaType"/> rozlišuje typ — výchozí <c>"image"</c> kvůli zpětné
/// kompatibilitě (všechny dosavadní záznamy jsou obrázky).
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
    DateTime GeneratedAt,
    string   MediaType = MediaTypes.Image)
{
    /// <summary>True, když jde o video (ne obrázek).</summary>
    public bool IsVideo => string.Equals(MediaType, MediaTypes.Video, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Hodnoty pro <see cref="ImageRecord.MediaType"/> (ukládají se do DB jako text).</summary>
public static class MediaTypes
{
    public const string Image = "image";
    public const string Video = "video";
}
