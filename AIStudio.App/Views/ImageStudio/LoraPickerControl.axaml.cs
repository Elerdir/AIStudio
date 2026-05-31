using Avalonia.Controls;

namespace AIStudio.App.Views.ImageStudio;

/// <summary>
/// Sdílený LoRA picker pro Smart i Manuální mód Image Studia. Viz XAML komentář
/// pro detaily o DataContextu (ImageGeneratorViewModel) a binding cestách.
/// </summary>
public partial class LoraPickerControl : UserControl
{
    public LoraPickerControl() => InitializeComponent();
}
