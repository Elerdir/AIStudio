namespace AIStudio.Core.Models;

/// <summary>
/// Jednotná projekce modelu napříč zdroji (HF + Civitai). UI pracuje
/// jen s tímhle — discovery service zaobalí provider-specific detaily.
///
/// `DownloadUrl` je už autorizované (s tokenem v query nebo bez, podle dostupnosti).
/// `FileName` je doporučené jméno pro uložení do <c>Models/</c>.
/// </summary>
public sealed record DiscoveredModel(
    string        Provider,         // "HuggingFace" | "Civitai"
    string        ProviderRef,      // HF: "owner/repo", Civitai: "12345" (model id jako string)
    string        Name,             // čisté jméno pro UI ("magnum-v4-22b" / "Juggernaut XL")
    string        Author,
    string        Description,      // krátký popisek (1-3 věty)
    string        FileName,         // jak se uloží na disk
    string        DownloadUrl,
    string        ModelPageUrl,
    long          SizeBytes,        // 0 když API nevrací velikost
    long          Downloads,
    double        Rating,           // 0-5 u Civitai, 0 u HF
    bool          Nsfw,
    string?       ThumbnailUrl,
    string?       BaseModel,        // pro UI ("SDXL 1.0", "FLUX.1 Schnell" …)
    string?       FileFormat,       // "GGUF", "SafeTensor", …
    string?       Sha256 = null,    // SHA-256 hash pro ověření po stažení (z Civitai API)
    string?       ModelType = null  // Civitai typ ("LORA"/"Checkpoint"/"VAE"…) — řídí cílovou podsložku
);
