using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Serilog;
using AIStudio.Infrastructure.Services;

namespace AIStudio.App.Views.Chat;

/// <summary>
/// Plný náhled vygenerovaného obrázku. Otevírá se z chat bubliny kliknutím
/// na <c>Image</c> nebo přes context menu.
///
/// <para>Funkce:</para>
/// <list type="bullet">
/// <item>Esc / klik na pozadí zavře</item>
/// <item>Tlačítko "Otevřít" → výchozí systémový prohlížeč obrázků</item>
/// <item>Tlačítko "Uložit jako…" → StorageProvider.SaveFilePickerAsync</item>
/// </list>
/// </summary>
public partial class ImageZoomWindow : Window
{
    private string? _imagePath;

    public ImageZoomWindow()
    {
        InitializeComponent();
        // Esc zavírá. KeyBinding na okno selhával s compiled bindings — KeyDown
        // handler je jednoduchý a spolehlivý.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    /// <summary>Otevře obrázek z disku do okna.</summary>
    public void Load(string imagePath)
    {
        _imagePath = imagePath;

        var label = this.FindControl<TextBlock>("FileNameLabel");
        if (label is not null)
            label.Text = Path.GetFileName(imagePath);

        try
        {
            // Stream-based load + okamžitě dispose stream, ať soubor nezůstane locknutý
            // (uživatel ho může chtít smazat z galerie, nebo přesunout).
            using var stream = File.OpenRead(imagePath);
            var bmp = new Bitmap(stream);

            var host = this.FindControl<Image>("ImageHost");
            if (host is not null) host.Source = bmp;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ImageZoomWindow: nepovedlo se načíst {Path}", imagePath);
        }
    }

    // ── Event handlery ────────────────────────────────────────────────────────

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Background_Tapped(object? sender, TappedEventArgs e)
    {
        // Klik PŘÍMO na pozadí (Border), ne propagovaný z dětí, zavře okno.
        // Avalonia tapped event je raised i z bubble — zkontrolujme source.
        if (e.Source is Border) Close();
    }

    private void OpenExternal_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_imagePath) || !File.Exists(_imagePath)) return;
        try
        {
            PlatformShell.Open(_imagePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ImageZoomWindow: OpenExternal selhal");
        }
    }

    private async void SaveAs_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_imagePath) || !File.Exists(_imagePath)) return;

        try
        {
            var ext = Path.GetExtension(_imagePath).TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "png";

            var result = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title              = "Uložit obrázek jako…",
                SuggestedFileName  = Path.GetFileNameWithoutExtension(_imagePath),
                DefaultExtension   = ext,
                FileTypeChoices    = new[]
                {
                    new FilePickerFileType($"Obrázek ({ext.ToUpperInvariant()})")
                    {
                        Patterns = new[] { $"*.{ext}" },
                    }
                }
            });

            if (result is null) return;

            // StorageFile může být na vzdáleném umístění — používáme jeho stream API,
            // ne LocalPath (které by mohlo být null pro cloud / OneDrive / atd.).
            await using var input  = File.OpenRead(_imagePath);
            await using var output = await result.OpenWriteAsync();
            await input.CopyToAsync(output);

            Log.Information("ImageZoomWindow: obrázek uložen do {Path}", result.Name);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ImageZoomWindow: SaveAs selhal");
        }
    }
}
