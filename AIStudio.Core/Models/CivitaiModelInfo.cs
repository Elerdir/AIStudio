namespace AIStudio.Core.Models;

/// <summary>
/// Top-level Civitai model — výsledek <c>/api/v1/models</c>. Odpovídá tomu,
/// co Civitai vrací v poli <c>items[]</c>. Pro UI a discovery service stačí
/// takhle plochá projekce; detail (<c>/api/v1/models/{id}</c>) má bohatší
/// strukturu, kterou ale potřebujeme jen ad-hoc.
/// </summary>
public record CivitaiModelInfo(
    long                                Id,
    string                              Name,
    string                              Type,            // "Checkpoint", "LORA", "TextualInversion", "Hypernetwork", "AestheticGradient", "Controlnet", "Poses"…
    string                              Description,    // může obsahovat HTML — UI musí stripnout
    string                              Creator,
    bool                                Nsfw,
    long                                DownloadCount,
    double                              Rating,
    int                                 RatingCount,
    string?                             ThumbnailUrl,    // první obrázek první verze, pokud je
    IReadOnlyList<CivitaiModelVersion>  Versions);

/// <summary>
/// Jedna verze modelu — Civitai modely jsou versioned (autor publikuje vN s
/// novými trenováními, fixy). Každá verze má vlastní files a base model.
/// </summary>
public record CivitaiModelVersion(
    long                              Id,
    string                            Name,             // "v1.0", "Final", …
    string                            BaseModel,        // "SD 1.5", "SDXL 1.0", "Pony", "Flux.1 D", "Illustrious"
    DateTime                          PublishedAt,
    IReadOnlyList<CivitaiModelFile>   Files);

/// <summary>
/// Konkrétní soubor (.safetensors / .ckpt / .pt / .zip). Civitai u většiny
/// checkpointů preferuje .safetensors, ale archivy s embeddingy mohou být .zip.
/// </summary>
public record CivitaiModelFile(
    long    Id,
    string  Name,                     // např. "model.safetensors"
    string  Type,                     // "Model", "VAE", "Config", "Training Data", "Negative", "Archive"
    long    SizeKb,                   // velikost v KB (Civitai vrací KB, ne bytes)
    string  Format,                   // "SafeTensor", "PickleTensor", "Other"
    string  DownloadUrl,              // přímý download (Civitai vyžaduje token v Authorization header)
    bool    Primary);                 // true = hlavní soubor verze (typicky checkpoint .safetensors)
