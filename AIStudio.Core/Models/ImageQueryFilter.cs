namespace AIStudio.Core.Models;

/// <summary>
/// Filtr pro dotaz na galerii obrázků. Každé pole je volitelné — <c>null</c>/prázdné
/// znamená „bez omezení". Podmínky se skládají v SQL přes <c>AND</c>.
///
/// <para><see cref="Search"/> hledá zároveň v promptu i v názvu modelu (LIKE).
/// <see cref="ModelName"/> filtruje na přesný název modelu (z dropdownu).
/// <see cref="From"/>/<see cref="To"/> omezují datum vygenerování (uloženo jako ISO 8601,
/// takže lexikografické porovnání odpovídá chronologii).</para>
/// </summary>
public sealed record ImageQueryFilter(
    string?   Search    = null,
    string?   ModelName = null,
    DateTime? From      = null,
    DateTime? To        = null)
{
    /// <summary>Prázdný filtr — žádné omezení (ekvivalent původního chování).</summary>
    public static readonly ImageQueryFilter None = new();

    /// <summary>True, když filtr nic neomezuje (lze přeskočit WHERE klauzuli).</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Search) &&
        string.IsNullOrWhiteSpace(ModelName) &&
        From is null && To is null;
}
