using AIStudio.Core.Enums;

namespace AIStudio.Core.Models;

public class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public AppLanguage Language { get; set; } = AppLanguage.Czech;
    public string ModelsDirectory { get; set; } = string.Empty;

    /// <summary>Průvodce prvním spuštěním byl dokončen.</summary>
    public bool SetupCompleted { get; set; } = false;

    /// <summary>Využívat GPU při inferenci (LlamaSharp gpuLayers=-1 vs 0).</summary>
    public bool UseGpu { get; set; } = true;

    /// <summary>
    /// Velikost kontextového okna (KV cache) v tokenech, předávaná do LlamaSharp
    /// při <c>LoadModelAsync</c>. Určuje kolik tokenů celé konverzace (system + historie
    /// + nový dotaz + místo na odpověď) si model pamatuje najednou. Při překročení
    /// <c>LlamaService.ChatAsync</c> tiše ořezává nejstarší ne-system zprávy.
    ///
    /// Změna se projeví až po **reloadu modelu** (LlamaSharp ContextSize je per-load).
    /// Větší hodnota = více VRAM (KV cache roste lineárně s ctx × num_layers).
    /// Doporučené hodnoty: 4096 (úspora VRAM), 8192 (výchozí balance),
    /// 16384/32768 (dlouhé konverzace, dostatek VRAM).
    /// </summary>
    public int ChatContextSize { get; set; } = 8192;

    /// <summary>
    /// Uživatel potvrdil pravidla použití při tréninku LoRA (etika tréninku
    /// na fotky cizích osob, nezletilých, atd.). Dialog se ukazuje jen jednou —
    /// při prvním kliknutí na „Spustit trénink". Po souhlasu se hodnota nastaví
    /// na true a další pokusy o trénink rovnou jedou.
    /// </summary>
    public bool LoraTrainingCodeOfConductAccepted { get; set; } = false;

    /// <summary>
    /// Výchozí model pro nové konverzace — nastavuje se v Model Manageru tlačítkem „Nastavit výchozí".
    /// Prázdný řetězec = použít první dostupný model.
    /// </summary>
    public string DefaultChatModelName { get; set; } = string.Empty;

    /// <summary>
    /// Civitai API klíč pro stahování modelů vyžadujících přihlášení.
    /// Nastaví se v Nastavení → Stahování.
    /// </summary>
    public string CivitaiApiKey { get; set; } = string.Empty;

    /// <summary>
    /// HuggingFace token pro stahování privátních modelů (nepovinné pro veřejné).
    /// </summary>
    public string HuggingFaceToken { get; set; } = string.Empty;

    // ── ComfyUI ───────────────────────────────────────────────────────────────

    /// <summary>Cesta ke složce s ComfyUI (kde je main.py).</summary>
    public string ComfyUiDirectory { get; set; } = string.Empty;

    /// <summary>Port na kterém ComfyUI naslouchá. Výchozí 8188.</summary>
    public int ComfyUiPort { get; set; } = 8188;

    /// <summary>Automaticky spustit ComfyUI při startu aplikace.</summary>
    public bool AutoStartComfyUi { get; set; } = false;

    /// <summary>Cesta k Python interpreteru. Prázdná = hledat v PATH.</summary>
    public string PythonPath { get; set; } = string.Empty;

    // ── Aktualizace ───────────────────────────────────────────────────────────

    /// <summary>
    /// Povolit automatickou kontrolu aktualizací proti UpdateHub serveru
    /// (https://updatehub.niderle.cz). Default = false dokud nepotvrdíme, že
    /// server běží a vrací správný manifest pro aplikaci „ai-studio".
    /// </summary>
    public bool CheckForUpdates { get; set; } = false;

    /// <summary>
    /// Update channel — "stable" (výchozí), "beta", "alpha". UpdateHub vrací
    /// nejnovější release v daném kanálu, takže beta-testeři dostávají preview.
    /// </summary>
    public string UpdateChannel { get; set; } = "stable";

    // ── First-run wizard handoff ──────────────────────────────────────────────

    /// <summary>
    /// ID doporučených modelů, které uživatel ve wizardu označil k stažení.
    /// Po dokončení wizardu App vyzvedne tento seznam, spustí stahování přes
    /// <see cref="Interfaces.IDownloadService"/> a seznam vyprázdní.
    /// IDs odpovídají <c>RecommendedModels.All[i].Id</c>.
    /// </summary>
    public List<string> PendingModelDownloads { get; set; } = new();

    // ── Chat → image gen preference ───────────────────────────────────────────

    /// <summary>
    /// String reprezentace <c>ImageKind</c> enumu — kindy, pro které uživatel
    /// označil "Už mi tu nabídku neukazuj" v chat → image gen recommender flow.
    /// Recommender tyto kindy přeskočí a tiše vrátí lokální match.
    /// </summary>
    public List<string> IgnoredImageUpgradeKinds { get; set; } = new();
}
