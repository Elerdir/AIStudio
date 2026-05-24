namespace AIStudio.Core.Models;

/// <summary>
/// Nabídka stáhnout lepší (nesažený) model než to, co uživatel má lokálně.
/// Vytvořeno recommenderem, předáno orchestrátorem do UI callbacku, kde se
/// uživatel rozhoduje (viz <see cref="UpgradeChoice"/>).
///
/// <para>Informace dostatečné pro to, aby UI uživateli umělo říct: "Pro
/// fotorealistické scény máme lepší model FLUX Schnell (6.8 GB) — stáhnout
/// na pozadí?". <see cref="Reason"/> přímo popisuje proč.</para>
/// </summary>
public sealed record ModelUpgradeOffer(
    /// <summary>Stabilní ID — odpovídá <c>RecommendedModel.Id</c> v katalogu.</summary>
    string  Id,
    /// <summary>Lidsky čitelný název ("FLUX Schnell Q4 GGUF").</summary>
    string  Name,
    /// <summary>Jedna věta proč — pro tlačítko v dialogu.</summary>
    string  Reason,
    /// <summary>Velikost v bytech — UI z toho udělá "6.8 GB".</summary>
    long    SizeBytes,
    /// <summary>Přímá HTTPS URL ke stažení.</summary>
    string  DownloadUrl,
    /// <summary>Cílový název v Models složce.</summary>
    string  FileName,
    /// <summary>SHA-256 hex string pro verifikaci (null = bez kontroly).</summary>
    string? Sha256,
    /// <summary>True pokud download vyžaduje HuggingFace token (gated repo).</summary>
    bool    RequiresHuggingFaceToken,
    /// <summary>Pro který <see cref="ImageKind"/> byla nabídka vystavena.
    /// UI to potřebuje pro "už mi to nenavrhuj pro tento typ" volbu.</summary>
    ImageKind Kind);

/// <summary>Volba uživatele v reakci na <see cref="ModelUpgradeOffer"/>.</summary>
public enum UpgradeChoice
{
    /// <summary>Pokračovat s lokálním modelem — nestahovat.</summary>
    UseLocal,
    /// <summary>Stáhnout nabídnutý model, počkat, pak generovat s ním.</summary>
    DownloadBetter,
    /// <summary>Zrušit celou operaci.</summary>
    Cancel,
}

/// <summary>
/// Výsledek z <c>IImageModelRecommender.RecommendAsync</c>. Pokud
/// <see cref="Upgrade"/> je null, znamená to, že uživatelův lokální výběr je
/// už optimální (nebo nemáme nic lepšího v katalogu). Pokud je non-null,
/// orchestrátor by měl uživateli ukázat dialog s touto nabídkou.
/// </summary>
public sealed record ImageModelRecommendation(
    string?            LocalBestMatch,
    ModelUpgradeOffer? Upgrade);
