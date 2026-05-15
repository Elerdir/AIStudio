using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Statická knihovna doporučených modelů — hardcoded seznam, který wizard nabízí
/// uživateli. Cílíme na 1 LLM + 1 image generator, ať uživatel po wizardu
/// **nemusí ručně hledat**, co stáhnout.
///
/// Kritéria výběru:
///   • Chat: kompaktní, kvalitní v češtině, ~5 GB (vejde se do 8GB VRAM),
///     OK licence pro distribuci.
///   • Image: malý a rychlý (Schnell varianta FLUXu) — uživatel uvidí
///     výsledek za sekundy, ne minuty.
///   • Bez gated repo flagu pokud možno — wizard ještě nemusí mít HF token.
///
/// **Pozor:** SHA-256 hashe je nutné aktualizovat při změně URL nebo když
/// HuggingFace přerazí soubor. Bez správného hashe DownloadService zruší
/// stažení a smaže polovinu staženého souboru.
/// </summary>
public static class RecommendedModels
{
    /// <summary>
    /// Llama 3.1 8B Instruct v GGUF Q4_K_M kvantizaci.
    /// Velikost ~4.9 GB, vejde se do RTX 3060/3070 s 8 GB VRAM.
    /// Mirror od bartowski (HF), který má quant skripty + dobré výsledky.
    /// </summary>
    public static readonly RecommendedModel Llama31_8B_Instruct = new(
        Id:                       "llama-3.1-8b-instruct-q4km",
        Name:                     "Llama 3.1 8B Instruct (Q4_K_M)",
        Description:              "Vyvážený chat model — rychlý, dobře umí česky, vejde se do 8 GB VRAM.",
        Kind:                     RecommendedModelKind.Chat,
        SizeBytes:                4_920_000_000L,
        DownloadUrl:              "https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF/resolve/main/Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf",
        FileName:                 "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf",
        // SHA-256 záměrně null — bartowski hashes nedodává v repo metadatech.
        // DownloadService bez hashe pouze stáhne; uživatel uvidí progress.
        // TODO: zařadit SHA-256 do RecommendedModel až bude perzistované checksum repo.
        Sha256:                   null,
        RequiresHuggingFaceToken: false);

    /// <summary>
    /// FLUX.1 Schnell v GGUF Q4_0 kvantizaci od city96.
    /// Velikost ~6.8 GB. Schnell varianta je rychlá (4 kroky), Apache-2.0 licence.
    /// Vyžaduje samostatné CLIP-L + T5 + VAE soubory (FluxDependencyService je stáhne).
    /// </summary>
    public static readonly RecommendedModel FluxSchnell_Q4 = new(
        Id:                       "flux1-schnell-q4-gguf",
        Name:                     "FLUX.1 Schnell (Q4_0 GGUF)",
        Description:              "Rychlý generátor obrázků — 4 kroky stačí. Apache-2.0 licence, bez registrace.",
        Kind:                     RecommendedModelKind.Image,
        SizeBytes:                6_810_000_000L,
        DownloadUrl:              "https://huggingface.co/city96/FLUX.1-schnell-gguf/resolve/main/flux1-schnell-Q4_0.gguf",
        FileName:                 "flux1-schnell-Q4_0.gguf",
        Sha256:                   null,
        RequiresHuggingFaceToken: false);

    /// <summary>Kompletní seznam doporučených modelů — pro UI iteraci.</summary>
    public static readonly IReadOnlyList<RecommendedModel> All = new[]
    {
        Llama31_8B_Instruct,
        FluxSchnell_Q4,
    };

    /// <summary>Najde model podle <see cref="RecommendedModel.Id"/>. Vrací null pokud neexistuje.</summary>
    public static RecommendedModel? FindById(string id) =>
        All.FirstOrDefault(m => m.Id == id);
}
