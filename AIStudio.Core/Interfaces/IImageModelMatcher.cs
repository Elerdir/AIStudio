using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Z lokálně dostupných checkpointů vybere ten nejvhodnější pro daný
/// <see cref="ImageKind"/>. Heuristika podle filename — žádné metadata
/// se nečtou (zatím).
/// </summary>
public interface IImageModelMatcher
{
    /// <summary>
    /// Vybere model. Pokud žádný kandidát neodpovídá kindu, fallbackuje
    /// na první dostupný .safetensors / .gguf. Když je seznam prázdný,
    /// vrací null — UI by mělo vyzvat uživatele ke stažení.
    /// </summary>
    /// <param name="kind">Co chceme generovat.</param>
    /// <param name="availableModels">Seznam jmen modelů z ImageGeneratorViewModel.AvailableCheckpoints.</param>
    /// <returns>Jméno modelu (s příponou) nebo null.</returns>
    string? Match(ImageKind kind, IReadOnlyList<string> availableModels);
}
