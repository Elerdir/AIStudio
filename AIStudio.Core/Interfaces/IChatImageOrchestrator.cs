using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Vysoký orchestrátor pro chat → image generation flow. Vstup je volný česky
/// zadaný popis, výstup je hotový soubor na disku + záznam v galerii.
///
/// <para>Pipeline:</para>
/// <list type="number">
/// <item>Parse intent (<see cref="IImageIntentParser"/>) — czechPrompt →
///   strukturovaný <see cref="ImageIntent"/> (EN prompt, kind, aspect, …).</item>
/// <item>Match model (<see cref="IImageModelMatcher"/>) — vybere konkrétní
///   stažený checkpoint dle kind.</item>
/// <item>Postaví Comfy workflow (txt2img nebo img2img dle reference).</item>
/// <item>Queue + wait pro výsledek z ComfyUI.</item>
/// <item>Stáhne obrázek do <c>%AppData%/AIStudio/Images/</c>.</item>
/// <item>Uloží do galerie (<see cref="IImageRepository"/>) jako kdyby byl
///   vygenerovaný v Image Studiu — uživatel ho najde i tam.</item>
/// </list>
///
/// <para>Nikdy nehodí výjimku — všechno se převede na <see cref="ChatImageGenerationResult"/>
/// s <c>Success=false</c>. UI to zobrazí jako chybu v bublině.</para>
/// </summary>
public interface IChatImageOrchestrator
{
    /// <summary>
    /// Vygeneruje obrázek z volného popisu.
    /// </summary>
    /// <param name="czechPrompt">Surový popis od uživatele (česky / anglicky / mix).</param>
    /// <param name="referenceImagePath">
    /// Pokud zadáno, použije se jako img2img reference (např. follow-up úprava
    /// předchozího obrázku v chatu). null = txt2img od nuly.
    /// </param>
    /// <param name="progress">Progress 0–100 pro UI.</param>
    /// <param name="askForUpgrade">
    /// Optional callback — orchestrátor ho zavolá, pokud recommender najde
    /// lepší online model, který uživatel nemá. UI by mělo zobrazit dialog
    /// a vrátit <see cref="UpgradeChoice"/>. Pokud callback není zadán
    /// (null), orchestrátor použije lokální model bez ptaní.
    /// </param>
    /// <param name="downloadProgress">
    /// Optional progress pro samostatné fáze: stahování modelu (pokud uživatel
    /// zvolil DownloadBetter) a načítání modelu. UI to typicky napojí na
    /// jiný indicator než main progress (aby uživatel viděl "stahuju 45 %"
    /// a později "generuju 30 %").
    /// </param>
    /// <param name="ct">Cancellation pro Stop tlačítko v chatu.</param>
    Task<ChatImageGenerationResult> GenerateAsync(
        string                                                       czechPrompt,
        string?                                                      referenceImagePath,
        IProgress<int>?                                              progress,
        CancellationToken                                            ct,
        Func<ModelUpgradeOffer, CancellationToken, Task<UpgradeChoice>>? askForUpgrade    = null,
        IProgress<DownloadStatusUpdate>?                              downloadProgress  = null,
        IProgress<ChatImageGenStage>?                                 stageProgress     = null);

    /// <summary>
    /// Vygeneruje osobu z referenční fotky obličeje v nové scéně dle promptu —
    /// PuLID-Flux, identita BEZ tréninku LoRA. Při prvním použití auto-instaluje
    /// PuLID stack. Když PuLID není dostupný / selže, vrátí <c>Success=false</c>.
    /// </summary>
    Task<ChatImageGenerationResult> GeneratePersonAsync(
        string                            czechPrompt,
        string                            faceImagePath,
        IProgress<int>?                   progress,
        CancellationToken                 ct,
        IProgress<DownloadStatusUpdate>?  downloadProgress = null,
        IProgress<ChatImageGenStage>?     stageProgress    = null);

    /// <summary>
    /// Upscale hotového obrázku (ESRGAN, ~2×) — pro „upscale ikonu" v chatu.
    /// Při prvním použití auto-stáhne upscale model (~64 MB). Nepoužívá LLM ani
    /// checkpoint. Výsledek se uloží do galerie jako nový obrázek.
    /// </summary>
    Task<ChatImageGenerationResult> UpscaleImageAsync(
        string                            imagePath,
        IProgress<int>?                   progress,
        CancellationToken                 ct,
        IProgress<DownloadStatusUpdate>?  downloadProgress = null);
}

/// <summary>
/// Update z download fáze — orchestrátor reportuje stahování lepšího modelu,
/// ať UI uživateli ukáže "stahuju Llama 3.1 8B 45 % (2.2 GB / 4.9 GB) · 12 MB/s".
/// </summary>
public sealed record DownloadStatusUpdate(
    string  ModelName,
    int     Percent,          // 0..100
    long    BytesDone,
    long    BytesTotal,
    double  MegabytesPerSecond);

/// <summary>
/// Hrubá fáze chat → image gen pipeline pro UI placeholder. Orchestrátor
/// reportuje přechody, aby uživatel věděl, jestli se zrovna hledá lepší model
/// (recommender + live search může trvat 1-3 s) nebo už běží faktické generování.
/// </summary>
public enum ChatImageGenStage
{
    /// <summary>Probíhá parser + recommender (curated + live search).</summary>
    Recommending,
    /// <summary>Queue do ComfyUI + čekání na výsledek + download bytů.</summary>
    Generating,
}
