using Avalonia.Controls;

namespace AIStudio.App.Views.ImageStudio;

public partial class ImageStudioPageView : UserControl
{
    public ImageStudioPageView()
    {
        InitializeComponent();

        // TODO: Drag & Drop pro reference image.
        // Avalonia 12 dramaticky změnila DragDrop API (DataTransfer / DataFormat /
        // Items), ale konkrétní metody pro získání IStorageFile z eventu se mezi
        // minor verzemi mění. Zatím zde držíme jen file picker (tlačítko v UI),
        // drag&drop doplníme až budeme mít cíleně otestováno proti konkrétní verzi.
    }
}
