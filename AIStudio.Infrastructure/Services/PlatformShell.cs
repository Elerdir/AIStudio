using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Cross-platform helper pro systémové akce: otevři složku v file manageru,
/// otevři soubor v default aplikaci, „reveal" soubor v containeru (vysvitne ho
/// označeného mezi sourozenci).
///
/// Nahrazuje <c>Process.Start("explorer.exe", ...)</c> volání rozesetá v kódu.
/// Bez tohoto by appka na macOS / Linuxu při klikání na „Otevřít složku"
/// vyhazovala FileNotFoundException — explorer.exe tam neexistuje.
/// </summary>
public static class PlatformShell
{
    /// <summary>
    /// Otevře cestu (soubor nebo složku) v default aplikaci OS.
    /// Win: explorer.exe. macOS: open. Linux: xdg-open.
    /// Selhání jen logujeme — uživatel může mít zablokované shell volání,
    /// ale to nemá shazovat aplikaci.
    /// </summary>
    public static void Open(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            Log.Warning("PlatformShell.Open: prázdná cesta");
            return;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // `open` — Apple's universal launcher. Adresář otevře ve Finderu,
                // soubor v default aplikaci podle UTI.
                Process.Start("open", $"\"{path}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", $"\"{path}\"");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PlatformShell.Open '{Path}' selhalo", path);
        }
    }

    /// <summary>
    /// „Reveal" — otevře container souboru a označí ho. Užitečné pro
    /// „ukaž tenhle vygenerovaný obrázek v Průzkumníkovi/Finderu".
    /// Když OS nepodporuje highlight, fallbackujeme na otevření containeru.
    /// </summary>
    public static void Reveal(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            Log.Warning("PlatformShell.Reveal: prázdná cesta");
            return;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // /select,"path" — otevře container a označí soubor
                Process.Start(new ProcessStartInfo
                {
                    FileName        = "explorer.exe",
                    Arguments       = $"/select,\"{filePath}\"",
                    UseShellExecute = false,
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // open -R — Reveal in Finder (otevře container + highlight)
                Process.Start("open", $"-R \"{filePath}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Linux nemá univerzální „reveal" — otevřeme jen container
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                    Process.Start("xdg-open", $"\"{dir}\"");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PlatformShell.Reveal '{Path}' selhalo", filePath);
        }
    }

    /// <summary>
    /// Otevře URL v default browseru. Cross-platform nahradka za
    /// <c>Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })</c>.
    /// </summary>
    public static void OpenUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            Log.Warning("PlatformShell.OpenUrl: prázdné URL");
            return;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", $"\"{url}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", $"\"{url}\"");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PlatformShell.OpenUrl '{Url}' selhalo", url);
        }
    }
}
