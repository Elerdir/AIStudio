namespace AIStudio.App.ViewModels.Chat;

/// <summary>
/// UI override pro automatickou detekci image intentu — slouží pro toggle
/// ikonku 🎨 v chat inputu. Default je <see cref="Auto"/> (keyword detektor
/// rozhodne); uživatel může explicitně přepnout, když chce mít kontrolu.
/// </summary>
public enum ChatImageMode
{
    /// <summary>Detektor klasifikuje každou zprávu — chat / image / edit.</summary>
    Auto,

    /// <summary>Každá zpráva půjde do image generation flow.</summary>
    ForceImage,

    /// <summary>Image gen vypnut — všechno do LLM, i když by detektor řekl image.</summary>
    ForceChat,
}
