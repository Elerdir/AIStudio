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
}
