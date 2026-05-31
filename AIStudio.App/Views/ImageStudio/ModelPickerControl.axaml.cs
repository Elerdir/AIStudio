using Avalonia.Controls;

namespace AIStudio.App.Views.ImageStudio;

/// <summary>
/// Sdílený checkpoint/model picker pro Smart i Manuální mód Image Studia.
/// DataContext = ImageGeneratorViewModel (viz XAML komentář).
/// </summary>
public partial class ModelPickerControl : UserControl
{
    public ModelPickerControl() => InitializeComponent();
}
