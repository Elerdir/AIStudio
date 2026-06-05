namespace AIStudio.Core.Models;

/// <summary>
/// Výjimka pro případ, kdy GGUF soubor modelu neexistuje na disku.
/// Odlišuje se od <see cref="OperationCanceledException"/>, aby se chybová bublina
/// neukládala do DB (po stažení modelu uživatel neuvidí starou chybovou zprávu).
/// </summary>
public sealed class ModelNotAvailableException(string modelName)
    : Exception($"Model '{modelName}' není stažen.")
{
    /// <summary>Název modelu, který chybí na disku.</summary>
    public string ModelName { get; } = modelName;
}
