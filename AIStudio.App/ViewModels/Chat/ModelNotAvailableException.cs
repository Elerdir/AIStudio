namespace AIStudio.App.ViewModels.Chat;

/// <summary>
/// Výjimka pro případ, kdy GGUF soubor modelu neexistuje na disku.
/// Odlišuje se od OperationCanceledException, aby se chybová bublina neukládala do DB.
/// </summary>
internal sealed class ModelNotAvailableException(string modelName)
    : Exception($"Model '{modelName}' není stažen.");
