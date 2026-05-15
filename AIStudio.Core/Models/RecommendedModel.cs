namespace AIStudio.Core.Models;

/// <summary>
/// Kategorie doporučeného modelu pro filtrování v UI / target složku.
/// </summary>
public enum RecommendedModelKind
{
    /// <summary>LLM pro chat (GGUF formát, ukládá se do kořene Models).</summary>
    Chat,
    /// <summary>Difúzní model pro generování obrázků (Safetensors / GGUF).</summary>
    Image
}

/// <summary>
/// Hardcoded doporučení modelu, které wizard nabízí uživateli při prvním spuštění
/// (a teoreticky lze i později v UI Modely → „Doporučené"). Použije se přes
/// IDownloadService → stažení do Models adresáře.
///
/// SHA-256 je povinný — bez něj nemůžeme ověřit integritu po stažení.
/// </summary>
/// <param name="Id">Stabilní ID pro perzistenci v <see cref="AppSettings.PendingModelDownloads"/>.</param>
/// <param name="Name">Krátký název pro UI (např. „Llama 3.1 8B Instruct").</param>
/// <param name="Description">Jedno-věta vysvětlení k čemu je dobrý.</param>
/// <param name="Kind">Chat (LLM) nebo Image (difúzní).</param>
/// <param name="SizeBytes">Velikost souboru — UI z toho udělá „4.6 GB".</param>
/// <param name="DownloadUrl">Přímá HTTPS URL — Civitai/HuggingFace resolve/main.</param>
/// <param name="FileName">Cílový název souboru v Models adresáři.</param>
/// <param name="Sha256">Hash hex stringem pro DownloadService.expectedSha256. Null = bez ověření (nedoporučeno).</param>
/// <param name="RequiresHuggingFaceToken">Pokud true, vyžaduje HF token pro stažení (gated repo).</param>
public sealed record RecommendedModel(
    string                Id,
    string                Name,
    string                Description,
    RecommendedModelKind  Kind,
    long                  SizeBytes,
    string                DownloadUrl,
    string                FileName,
    string?               Sha256 = null,
    bool                  RequiresHuggingFaceToken = false);
