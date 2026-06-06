namespace AIStudio.Core.Services;

/// <summary>
/// Mapuje názvy samplerů z AI Studia / ComfyUI (např. <c>dpmpp_2m</c>, <c>euler_ancestral</c>)
/// na názvy, kterým rozumí <c>stable-diffusion.cpp</c> (<c>--sampling-method</c>: euler, euler_a,
/// heun, dpm2, dpm++2s_a, dpm++2m, dpm++2mv2, lcm, ddim_trailing, …). Čistá funkce — žádný stav,
/// plně testovatelná. Neznámý sampler → bezpečný default <see cref="Default"/>.
/// </summary>
public static class NativeSamplerMap
{
    /// <summary>Bezpečný výchozí sampler, když mapování selže.</summary>
    public const string Default = "euler";

    /// <summary>Mapuje sampler na sd.cpp název. Case-insensitive, ořezává mezery.</summary>
    public static string ToSdCpp(string? sampler)
    {
        if (string.IsNullOrWhiteSpace(sampler)) return Default;

        return sampler.Trim().ToLowerInvariant() switch
        {
            "euler"                                  => "euler",
            "euler_a" or "euler_ancestral"           => "euler_a",
            "heun"                                   => "heun",
            "dpm_2" or "dpm2"                        => "dpm2",
            "dpmpp_2s_ancestral" or "dpm++2s_a"      => "dpm++2s_a",
            "dpmpp_2m" or "dpm++2m"                  => "dpm++2m",
            "dpmpp_2m_sde" or "dpm++2mv2"            => "dpm++2mv2",
            "lcm"                                    => "lcm",
            "ddim" or "ddim_uniform" or "ddim_trailing" => "ddim_trailing",
            "ipndm"                                  => "ipndm",
            "ipndm_v"                                => "ipndm_v",
            // uni_pc, dpmpp_sde, dpmpp_3m a další bez přímého ekvivalentu → nejbližší rozumný default
            _                                        => Default,
        };
    }

    /// <summary>True když má sampler přímý ekvivalent v sd.cpp (ne jen fallback na default).</summary>
    public static bool HasDirectEquivalent(string? sampler)
    {
        if (string.IsNullOrWhiteSpace(sampler)) return false;
        var mapped = ToSdCpp(sampler);
        // Default fallback je „přímý“ jen když uživatel skutečně zadal euler.
        return mapped != Default || sampler.Trim().Equals("euler", StringComparison.OrdinalIgnoreCase);
    }
}
