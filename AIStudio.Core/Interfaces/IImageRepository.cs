using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

public interface IImageRepository
{
    Task InitializeAsync();
    Task SaveImageAsync(ImageRecord image);

    /// <summary>
    /// Načte VŠECHNY záznamy najednou. POUZE pro malé databáze / testy.
    /// Pro UI použij <see cref="LoadImagesPagedAsync"/>.
    /// </summary>
    Task<IReadOnlyList<ImageRecord>> LoadAllImagesAsync();

    /// <summary>
    /// Načte stránku záznamů seřazených od nejnovějšího, volitelně filtrovaných.
    /// </summary>
    /// <param name="skip">Kolik prvních záznamů přeskočit (offset).</param>
    /// <param name="take">Kolik záznamů načíst (limit).</param>
    /// <param name="filter">Volitelný filtr (prompt/model/datum). <c>null</c> = bez omezení.</param>
    Task<IReadOnlyList<ImageRecord>> LoadImagesPagedAsync(int skip, int take, ImageQueryFilter? filter = null);

    /// <summary>Celkový počet záznamů v DB (po případném filtru) — pro „X z Y" label.</summary>
    Task<int> CountImagesAsync(ImageQueryFilter? filter = null);

    /// <summary>
    /// Vrátí seznam unikátních názvů modelů (neprázdných), seřazený abecedně —
    /// pro nabídku filtru „Model" v galerii.
    /// </summary>
    Task<IReadOnlyList<string>> GetDistinctModelsAsync();

    Task DeleteImageAsync(string id);
}
