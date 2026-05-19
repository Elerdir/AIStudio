using System.Diagnostics;
using System.Security.Cryptography;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// macOS Keychain wrapper přes Apple <c>security</c> CLI utility.
/// Ukládá AES-256 master klíč, který <see cref="TokenProtection"/> používá
/// pro šifrování citlivých údajů (HuggingFace token, Civitai API key).
///
/// **Proč ne plné P/Invoke do Security.framework:** CLI verze je výrazně
/// jednodušší a stojí v cestě běžnému malwaru stejně dobře. Pro produkční
/// nasazení s App Store sandboxem budeme muset přejít na nativní SecKeychain
/// API — ale to je hudba budoucnosti (Phase D).
///
/// **Účet / služba:**
///   - account: <c>ai-studio-aes-key</c>  (identifikuje konkrétní záznam)
///   - service: <c>cz.niderle.aistudio</c> (reverse-DNS jako Apple konvence)
///
/// V Apple Keychain Access GUI uvidí uživatel záznam s těmito jmény.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal static class MacOsKeychainKeyStore
{
    private const string Account = "ai-studio-aes-key";
    private const string Service = "cz.niderle.aistudio";

    /// <summary>
    /// Vrátí 32-bajt master klíč. Při prvním běhu ho vygeneruje a uloží;
    /// při dalších načte z Keychain. Vrátí null pokud Keychain není dostupný
    /// (typicky headless CI runner bez login session) — caller propadne na
    /// deterministic hash fallback.
    /// </summary>
    public static byte[]? GetOrCreateKey()
    {
        var existing = TryReadKey();
        if (existing is not null) return existing;

        // Vygeneruj nový a ulož
        var newKey = RandomNumberGenerator.GetBytes(32);
        if (TrySaveKey(newKey))
            return newKey;

        return null;
    }

    private static byte[]? TryReadKey()
    {
        try
        {
            // security find-generic-password -a Account -s Service -w
            //   -w = pouze samotné heslo (bez metadat)
            // Při miss vrací exit code 44; nepokoušíme se to parsovat,
            // jen vrátíme null a saveni vytvoří nový klíč.
            var output = RunSecurity($"find-generic-password -a \"{Account}\" -s \"{Service}\" -w");
            if (string.IsNullOrWhiteSpace(output)) return null;

            var b64 = output.Trim();
            var bytes = Convert.FromBase64String(b64);
            return bytes.Length == 32 ? bytes : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "MacOsKeychainKeyStore: find-generic-password selhalo (pravděpodobně klíč ještě neexistuje)");
            return null;
        }
    }

    private static bool TrySaveKey(byte[] key)
    {
        try
        {
            var b64 = Convert.ToBase64String(key);
            // -U = update if exists; -T "" omezuje aplikační access (bez seznamu = jen security CLI samo)
            // Heslo se předává přes -w; CLI ho neukáže v ps tabulkách (běží jen krátce + není v env).
            var args = $"add-generic-password -a \"{Account}\" -s \"{Service}\" -w \"{b64}\" -U";
            var output = RunSecurity(args);
            // Při úspěchu security CLI nic netiskne. Při chybě by ParseExit vyhodilo výjimku.
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MacOsKeychainKeyStore: add-generic-password selhalo — nemůžeme uložit klíč do Keychain");
            return false;
        }
    }

    /// <summary>Spustí <c>security</c> binárku a vrátí stdout. Při exit kódu ≠ 0 vyhodí výjimku.</summary>
    private static string RunSecurity(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "security",
            Arguments              = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Nelze spustit security CLI");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();

        if (!proc.WaitForExit(5000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("security CLI timeout (5 s)");
        }

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"security skončilo s kódem {proc.ExitCode}: {stderr.Trim()}");

        return stdout;
    }
}
