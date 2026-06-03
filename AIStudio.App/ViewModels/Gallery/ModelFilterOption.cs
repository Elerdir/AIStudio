using System.IO;

namespace AIStudio.App.ViewModels.Gallery;

/// <summary>
/// Položka v dropdownu filtru „Model". <see cref="Value"/> je plný název modelu z DB
/// (nebo <c>null</c> pro „Všechny modely"), <see cref="Display"/> je zkrácený název bez
/// přípony/cesty pro zobrazení.
/// </summary>
public sealed record ModelFilterOption(string? Value, string Display)
{
    /// <summary>Sdílená instance „bez filtru" (porovnává se referenčně i hodnotou Value=null).</summary>
    public static readonly ModelFilterOption All = new(null, "Všechny modely");

    /// <summary>Vytvoří položku z plného názvu modelu se zkráceným zobrazením.</summary>
    public static ModelFilterOption For(string modelName)
    {
        string display;
        try { display = Path.GetFileNameWithoutExtension(modelName); }
        catch { display = modelName; }
        if (string.IsNullOrWhiteSpace(display)) display = modelName;
        return new ModelFilterOption(modelName, display);
    }
}
