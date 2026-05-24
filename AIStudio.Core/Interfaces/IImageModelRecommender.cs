using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Doporučuje konkrétní model pro daný <see cref="ImageIntent"/> — kombinuje
/// to, co má uživatel lokálně stažené, s katalogem doporučených modelů (a
/// volitelně live search po Civitai/HuggingFace).
///
/// <para>Vrací <see cref="ImageModelRecommendation"/>:
/// <list type="bullet">
/// <item><c>LocalBestMatch</c> — nejlepší dostupný lokální model (nebo null
///   pokud uživatel nemá žádný relevantní)</item>
/// <item><c>Upgrade</c> — non-null pokud existuje lepší online kandidát,
///   který uživatel nemá; orchestrátor by měl uživateli nabídnout dialog</item>
/// </list>
/// </para>
///
/// <para>Strategie pro MVP: curated-first. Použije se hardcoded <c>RecommendedModels</c>;
/// live Civitai/HF search přijde později jako fallback.</para>
/// </summary>
public interface IImageModelRecommender
{
    Task<ImageModelRecommendation> RecommendAsync(
        ImageIntent           intent,
        IReadOnlyList<string> localCheckpoints,
        CancellationToken     ct);
}
