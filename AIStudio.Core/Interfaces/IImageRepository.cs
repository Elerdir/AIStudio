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
    /// Načte stránku záznamů seřazených od nejnovějšího.
    /// </summary>
    /// <param name="skip">Kolik prvních záznamů přeskočit (offset).</param>
    /// <param name="take">Kolik záznamů načíst (limit).</param>
    Task<IReadOnlyList<ImageRecord>> LoadImagesPagedAsync(int skip, int take);

    /// <summary>Celkový počet záznamů v DB — pro „X z Y" pagination label.</summary>
    Task<int> CountImagesAsync();

    Task DeleteImageAsync(string id);
}
