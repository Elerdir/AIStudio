using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AIStudio.App.Views.Models;

/// <summary>
/// Samostatné okno s detailem vybraného modelu. Otevírá se kliknutím na
/// "Detail" tlačítko v Models seznamu — uvolňuje horizontální prostor
/// hlavnímu listu (předtím byl detail v pravém splitter panelu).
///
/// <para>DataContext = <c>ModelManagerPageViewModel</c> — bindings na
/// <c>SelectedModel.*</c> + commands jdou skrz <c>$parent[Window].DataContext</c>.</para>
/// </summary>
public partial class ModelDetailWindow : Window
{
    public ModelDetailWindow()
    {
        InitializeComponent();

        // Esc zavře — stejný pattern jako ImageZoomWindow
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
