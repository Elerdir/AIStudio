using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Pomocník pro spouštění sd-scripts Python skriptů přes ComfyUI embedded Python.
///
/// <para><b>Problém, který řeší:</b> sd-scripts skripty importují interní balík
/// <c>library</c> (<c>from library.device_utils import …</c>). Normálně Python
/// přidá adresář spouštěného skriptu do <c>sys.path[0]</c>, takže <c>import library</c>
/// najde <c>sd-scripts/library/</c>. ALE ComfyUI portable Python má vedle sebe
/// <c>pythonXX._pth</c> soubor — ten plně určuje <c>sys.path</c> a <b>vypíná</b>
/// automatické přidání adresáře skriptu i <c>PYTHONPATH</c> env proměnnou
/// (dokumentované chování CPythonu při přítomnosti <c>._pth</c>).</para>
///
/// <para><b>Řešení:</b> Místo přímého spuštění <c>python script.py</c> spustíme
/// <c>python _aistudio_launch.py script.py …args</c>. Launcher si do <c>sys.path</c>
/// ručně přidá kořen sd-scripts a teprve pak cílový skript spustí přes
/// <c>runpy.run_path(..., run_name="__main__")</c> — což zachová <c>argparse</c>
/// i <c>if __name__ == "__main__"</c> blok cílového skriptu.</para>
/// </summary>
internal static class SdScriptsLauncher
{
    private const string LauncherFileName = "_aistudio_launch.py";

    /// <summary>
    /// Obsah launcher skriptu. Idempotentně zapisujeme do sd-scripts kořene.
    /// </summary>
    private const string LauncherSource = """
        # AUTO-GENEROVÁNO AI Studiem — nemazat.
        # Přidá kořen sd-scripts do sys.path (ComfyUI embedded Python s ._pth
        # nepřidává adresář skriptu automaticky) a spustí cílový skript jako __main__.
        import sys, os, runpy

        here = os.path.dirname(os.path.abspath(__file__))
        if here not in sys.path:
            sys.path.insert(0, here)

        if len(sys.argv) < 2:
            print("launcher: chybí cesta k cílovému skriptu", file=sys.stderr)
            sys.exit(2)

        target = sys.argv[1]
        if not os.path.isabs(target):
            target = os.path.join(here, target)

        # Posuneme argv tak, aby cílový skript viděl svoje argumenty od indexu 0
        sys.argv = [target] + sys.argv[2:]

        runpy.run_path(target, run_name="__main__")
        """;

    /// <summary>
    /// Zapíše launcher do <paramref name="sdScriptsDir"/> (idempotentně —
    /// přepíše, pokud se obsah liší, jinak nedělá nic). Vrátí absolutní cestu
    /// k launcheru.
    /// </summary>
    public static string EnsureLauncher(string sdScriptsDir)
    {
        var path = Path.Combine(sdScriptsDir, LauncherFileName);
        try
        {
            // Přepíšeme jen pokud chybí nebo se liší — ať zbytečně nešaháme na disk
            // při každém spuštění a respektujeme případné budoucí změny launcheru.
            var needsWrite = !File.Exists(path) ||
                             File.ReadAllText(path) != LauncherSource;
            if (needsWrite)
                File.WriteAllText(path, LauncherSource);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SdScriptsLauncher: zápis {Path} selhal — import library může selhat", path);
        }
        return path;
    }
}
