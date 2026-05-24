using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AIStudio.App.ViewModels.Setup;
using Serilog;

namespace AIStudio.App.Views.Setup;

public partial class FirstRunWizardWindow : Window
{
    // ── Stav viditelnosti hesel ───────────────────────────────────────────────
    private bool _hfTokenVisible;
    private bool _civitaiKeyVisible;

    public FirstRunWizardWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Naváže VM a inicializuje průvodce.
    /// Volat ihned po přiřazení DataContext.
    /// </summary>
    public void Initialize(FirstRunWizardViewModel vm)
    {
        DataContext = vm;
        vm.WizardCompleted += (_, _) => Close();
        vm.Initialize();
    }

    // ── Prohlížeč složky (Step 1) ─────────────────────────────────────────────

    // async void je nutný pro event handler, ale veškerý await musí být v try/catch —
    // jinak by výjimka přistála v UnobservedTaskException až po GC a uživatel by viděl
    // tlačítko, které nereaguje. Storage picker může selhat (zrušený dialog, IO chyba).
    private async void BrowseBtn_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title         = "Vyberte složku pro uložení modelů",
                AllowMultiple = false,
            });

            if (result.Count > 0 && DataContext is FirstRunWizardViewModel vm)
                vm.SetModelsDirectory(result[0].Path.LocalPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FirstRunWizard: BrowseBtn_Click — výběr složky selhal");
        }
    }

    // ── Toggle viditelnost tokenů (Step 3) ────────────────────────────────────

    private void ToggleHfVis_Click(object? sender, RoutedEventArgs e)
    {
        _hfTokenVisible = !_hfTokenVisible;
        var box = this.FindControl<TextBox>("HfTokenBox");
        if (box is not null)
            box.PasswordChar = _hfTokenVisible ? '\0' : '•';
    }

    private void ToggleCivitaiVis_Click(object? sender, RoutedEventArgs e)
    {
        _civitaiKeyVisible = !_civitaiKeyVisible;
        var box = this.FindControl<TextBox>("CivitaiKeyBox");
        if (box is not null)
            box.PasswordChar = _civitaiKeyVisible ? '\0' : '•';
    }

    // ── GPU / CPU volba (Step 2) ──────────────────────────────────────────────

    private void GpuOption_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is FirstRunWizardViewModel vm)
            vm.UseGpu = true;
    }

    private void CpuOption_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is FirstRunWizardViewModel vm)
            vm.UseGpu = false;
    }
}
