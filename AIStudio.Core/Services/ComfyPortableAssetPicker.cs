using System.Text.RegularExpressions;

namespace AIStudio.Core.Services;

/// <summary>
/// Výběr správného ComfyUI Portable NVIDIA assetu z GitHub release podle generace GPU.
/// ComfyUI publikuje dvě NVIDIA varianty (viz README):
/// <list type="bullet">
/// <item><c>ComfyUI_windows_portable_nvidia.7z</c> — nejnovější PyTorch/CUDA (aktuálně
///   CUDA 13). Podporuje <b>RTX 20 a novější, včetně RTX 50 (Blackwell)</b>. Výchozí volba.</item>
/// <item><c>ComfyUI_windows_portable_nvidia_cu126.7z</c> — starší PyTorch s CUDA 12.6.
///   Pro <b>GTX 10 a starší</b> karty, které nové CUDA řady už nepodporují.</item>
/// </list>
///
/// <para>Dřívější logika preferovala nejvyšší <c>cuNNN</c> sufix a bezsufixový build
/// skórovala nulou — tím na moderních kartách vybírala legacy cu126 build, jehož torch
/// nemá sm_120 kernely → na RTX 50 „no kernel image available". Čistá funkce, testovaná.</para>
/// </summary>
public static partial class ComfyPortableAssetPicker
{
    [GeneratedRegex(@"cu(\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex CudaSuffixRegex();

    // GTX 10xx/9xx/7xx/6xx + GT xxx = Pascal/Maxwell/Kepler — CUDA 13 torch je
    // už nepodporuje, patří jim cu126 build. TITAN varianty (vzácné) neřešíme —
    // default je moderní build, což je bezpečnější směr chyby (novější karta
    // se starým buildem = rozbité; stará karta s novým = jasná chyba při startu).
    [GeneratedRegex(@"\bGTX?\s*(6|7|9|10)\d{2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyGpuRegex();

    /// <summary>
    /// True pro NVIDIA karty generace GTX 10 a starší (Pascal/Maxwell/Kepler),
    /// které potřebují legacy cu126 build. RTX cokoliv → false.
    /// </summary>
    public static bool IsLegacyNvidiaGpu(string? gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName)) return false;
        if (gpuName.Contains("RTX", StringComparison.OrdinalIgnoreCase)) return false;
        return LegacyGpuRegex().IsMatch(gpuName);
    }

    /// <summary>
    /// Vybere nejvhodnější NVIDIA portable asset z názvů v release.
    /// <paramref name="legacyGpu"/> = true pro GTX 10 a starší (viz
    /// <see cref="IsLegacyNvidiaGpu"/>). Vrací null, když žádný kandidát nesedí.
    /// </summary>
    public static string? PickBest(IReadOnlyList<string> assetNames, bool legacyGpu)
    {
        ArgumentNullException.ThrowIfNull(assetNames);

        string? best = null;
        var bestScore = int.MinValue;

        foreach (var name in assetNames)
        {
            if (!name.Contains("windows_portable", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.Contains("nvidia",           StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.EndsWith(".7z",              StringComparison.OrdinalIgnoreCase)) continue;

            var m = CudaSuffixRegex().Match(name);
            int score;
            if (legacyGpu)
            {
                // Stará karta: přesně cu126 je „ten pravý“; jiné cuNNN podle čísla
                // (nižší CUDA ~ větší šance na podporu), bezsufixový (CUDA 13) až poslední.
                score = !m.Success ? 1
                      : int.Parse(m.Groups[1].Value) == 126 ? 1000
                      : 500 - int.Parse(m.Groups[1].Value);
            }
            else
            {
                // Moderní karta (RTX 20+ vč. 50): bezsufixový build = nejnovější CUDA
                // s podporou Blackwell → nejvyšší priorita; jinak nejvyšší cuNNN.
                score = !m.Success ? 1000 : int.Parse(m.Groups[1].Value);
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = name;
            }
        }
        return best;
    }
}
