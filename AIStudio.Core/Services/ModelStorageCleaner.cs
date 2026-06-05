namespace AIStudio.Core.Services;

/// <summary>
/// Čistá logika úklidu úložiště modelů — hledání a mazání osiřelých <c>.tmp</c> souborů
/// (nedokončená/opuštěná stahování). <paramref name="protectedPaths"/> jsou cesty, které
/// se NESMÍ smazat (právě běžící nebo pozastavená stahování). Bez závislosti na UI/VM →
/// jde unit-testovat s temp adresářem.
/// </summary>
public static class ModelStorageCleaner
{
    /// <summary>Najde osiřelé <c>.tmp</c> (rekurzivně) mimo chráněné. Vrací počet a celkovou velikost.</summary>
    public static (int Count, long Bytes) ScanOrphanTmp(string? modelsDir, ISet<string> protectedPaths)
    {
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir)) return (0, 0);

        var count = 0;
        var bytes = 0L;
        foreach (var tmp in EnumerateTmp(modelsDir))
        {
            if (protectedPaths.Contains(tmp)) continue;
            count++;
            try { bytes += new FileInfo(tmp).Length; } catch { /* nedostupný — přeskoč velikost */ }
        }
        return (count, bytes);
    }

    /// <summary>Smaže osiřelé <c>.tmp</c> mimo chráněné. Vrací počet skutečně smazaných.</summary>
    public static int DeleteOrphanTmp(string? modelsDir, ISet<string> protectedPaths)
    {
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir)) return 0;

        var deleted = 0;
        foreach (var tmp in EnumerateTmp(modelsDir).ToList())
        {
            if (protectedPaths.Contains(tmp)) continue;
            try { File.Delete(tmp); deleted++; } catch { /* zamčený/antivir — přeskoč */ }
        }
        return deleted;
    }

    private static IEnumerable<string> EnumerateTmp(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.tmp", SearchOption.AllDirectories); }
        catch { return []; }
    }
}
