using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Zajišťuje, že je k dispozici Python prostředí + pip balíky + sd-scripts
/// repo potřebné pro trénink LoRA. Reuse-uje ComfyUI Python venv, aby
/// uživatel nemusel instalovat druhý Python — viz <see cref="IComfyInstaller"/>.
///
/// <para>Strategie:</para>
/// <list type="number">
/// <item>Najde ComfyUI Python přes <see cref="ISettingsService"/>+<see cref="IComfyInstaller"/>.</item>
/// <item>Zkontroluje, jestli má balíky: <c>accelerate</c>, <c>transformers</c>,
///   <c>diffusers</c>, <c>peft</c>, <c>bitsandbytes</c>, <c>safetensors</c>,
///   <c>lycoris-lora</c>, <c>prodigyopt</c>, <c>lion-pytorch</c>.</item>
/// <item>Pokud něco chybí, spustí <c>python -m pip install ...</c> přes embedded Python.</item>
/// <item>Naklonuje (nebo updatuje) <c>sd-scripts</c> z GitHubu do <c>%LocalAppData%\AIStudio\sd-scripts\</c>.</item>
/// </list>
///
/// <para>Idempotentní: pokud je vše OK, volání se vrátí ihned.</para>
/// </summary>
public interface ILoraTrainerDependencyService
{
    /// <summary>Stav probíhající instalace — true během EnsureAllAsync.</summary>
    bool IsInstalling { get; }

    /// <summary>Lidsky čitelný popis aktuální fáze („Stahuji bitsandbytes…").</summary>
    string CurrentStage { get; }

    /// <summary>
    /// Cesta k naklonovanému sd-scripts repu na disku. Null pokud zatím
    /// neproběhla instalace nebo selhala.
    /// </summary>
    string? SdScriptsDirectory { get; }

    /// <summary>
    /// True pokud jsou všechny pip balíky přítomny v ComfyUI venv.
    /// Volá <c>python -m pip show ...</c> pro každý balík — drahé,
    /// nevolat na UI threadu bez await.
    /// </summary>
    Task<bool> ArePipDependenciesInstalledAsync(string pythonExe, CancellationToken ct = default);

    /// <summary>
    /// True pokud sd-scripts adresář existuje a má <c>sdxl_train_network.py</c>.
    /// </summary>
    bool IsSdScriptsAvailable();

    /// <summary>
    /// Vrátí seznam chybějících závislostí — pro UI „před prvním tréninkem se
    /// stáhne X, Y, Z (~1.5 GB)" hlášku.
    /// </summary>
    Task<IReadOnlyList<string>> FindMissingAsync(string pythonExe, CancellationToken ct = default);

    /// <summary>
    /// Doinstaluje vše chybějící (pip balíky + sd-scripts). Idempotentní.
    /// První spuštění typicky stahuje ~1-2 GB a trvá 2-5 min podle linky.
    /// Progress emituje stage + případný DL byte counter pro torch.
    /// </summary>
    Task EnsureAllAsync(
        string                                 pythonExe,
        IProgress<LoraTrainerInstallProgress>? progress = null,
        CancellationToken                      ct       = default);
}
