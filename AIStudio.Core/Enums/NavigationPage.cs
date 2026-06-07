namespace AIStudio.Core.Enums;

public enum NavigationPage
{
    Chat,

    /// <summary>
    /// Sjednocený rozcestník „Tvorba" — v sidebaru jedno tlačítko, uvnitř záložky
    /// Obrázky / Videa / Galerie / Upscale. Hodnoty <see cref="ImageStudio"/>,
    /// <see cref="Video"/>, <see cref="Gallery"/> a <see cref="Upscale"/> zůstávají
    /// kvůli cílené navigaci mezi VM (např. „otevřít v Image Studiu") — směrují na
    /// konkrétní vnitřní záložku tohoto rozcestníku.
    /// </summary>
    Creation,
    ImageStudio,
    Video,
    Gallery,
    Upscale,
    Models,
    Lora,
    System,
    Settings
}
