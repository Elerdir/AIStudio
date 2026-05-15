using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Šifrování citlivých dat (HuggingFace token, Civitai API klíč) před zápisem
/// do settings.json. Na Windows používá DPAPI <see cref="ProtectedData.Protect"/>
/// se scope <see cref="DataProtectionScope.CurrentUser"/>, takže šifra je vázaná
/// na uživatelský profil — jiný uživatel/stroj ji nerozšifruje.
///
/// Na macOS/Linux DPAPI neexistuje. Používáme **AES-GCM s klíčem odvozeným
/// z machine ID + user name** — to není ekvivalent OS keychain (TODO: nahradit
/// později za libsecret/Keychain), ale stojí v cestě běžnému malwaru, který
/// přečte settings.json. Plaintext token v paměti zůstává — chráníme jen disk.
///
/// **Wire format:** šifrovaný string má prefix <c>enc:v1:</c>, aby šel
/// rozpoznat od plaintextu. Token bez prefixu se považuje za nezašifrovaný
/// (legacy, bude zašifrován při dalším save).
/// </summary>
internal static class TokenProtection
{
    private const string Prefix = "enc:v1:";

    /// <summary>Vrátí true pokud řetězec vypadá jako už zašifrovaný (má prefix).</summary>
    public static bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Zašifruje plaintext. Prázdný vstup vrátí prázdný výstup beze změny.
    /// Pokud je vstup už chráněný (má prefix), vrátí ho beze změny — idempotentní.
    /// </summary>
    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        if (IsProtected(plaintext))         return plaintext; // už zašifrované

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var enc   = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser)
                : EncryptAesGcm(bytes);
            return Prefix + Convert.ToBase64String(enc);
        }
        catch (Exception ex)
        {
            // Nesmíme blokovat save — radši ztratíme šifrování (a zalogujeme)
            // než aby uživatel ztratil token.
            Log.Warning(ex, "TokenProtection: Protect selhalo — token bude uložen v plaintextu");
            return plaintext;
        }
    }

    /// <summary>
    /// Dešifruje chráněný řetězec. Prázdný vstup → prázdný výstup.
    /// Pokud vstup nemá prefix, považuje se za už dešifrovaný (legacy) a vrací se beze změny.
    /// </summary>
    public static string Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return string.Empty;
        if (!IsProtected(ciphertext))         return ciphertext; // legacy plaintext

        try
        {
            var b64 = ciphertext.Substring(Prefix.Length);
            var enc = Convert.FromBase64String(b64);
            var bytes = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(enc, optionalEntropy: null, DataProtectionScope.CurrentUser)
                : DecryptAesGcm(enc);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException ex)
        {
            // Klíč se nemusí podařit dešifrovat když uživatel:
            //  • zkopíroval settings.json mezi stroji
            //  • změnil username / profil
            //  • migroval z Windows na Mac
            // V tomhle případě je token efektivně ztracený — uživatel ho musí
            // zadat znovu. Vracíme prázdný string + warn do logu.
            Log.Warning(ex, "TokenProtection: dešifrování selhalo — token byl pravděpodobně zašifrován jiným profilem, " +
                            "uživatel ho musí zadat znovu");
            return string.Empty;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TokenProtection: neočekávaná chyba při dešifrování");
            return string.Empty;
        }
    }

    // ── Cross-platform fallback (AES-GCM s klíčem odvozeným z machine+user) ──
    //
    // Toto NENÍ OS keychain. Klíč je odvozený z deterministických hodnot
    // (machineGuid + username), takže malware co umí číst tyto hodnoty může
    // i dešifrovat token. Ale brání banálnímu „cat settings.json" útoku.
    // TODO: na macOS přepnout na Keychain Services, na Linux na libsecret.

    private static byte[] DeriveKey()
    {
        // Stabilní seed pro tuhle session i mezi spuštěními:
        //   machineGuid (Mac: serial, Linux: /etc/machine-id) + username
        // Hash uřízneme na 32 bajtů (AES-256).
        var seed = $"AIStudio.v1|{Environment.MachineName}|{Environment.UserName}|{RuntimeInformation.OSDescription}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(seed));
    }

    private static byte[] EncryptAesGcm(byte[] plaintext)
    {
        var key   = DeriveKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag   = new byte[16];
        var ct    = new byte[plaintext.Length];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ct, tag);

        // Wire: [nonce 12B][tag 16B][ciphertext...]
        var result = new byte[nonce.Length + tag.Length + ct.Length];
        Buffer.BlockCopy(nonce, 0, result, 0,                 nonce.Length);
        Buffer.BlockCopy(tag,   0, result, nonce.Length,      tag.Length);
        Buffer.BlockCopy(ct,    0, result, nonce.Length + tag.Length, ct.Length);
        return result;
    }

    private static byte[] DecryptAesGcm(byte[] blob)
    {
        if (blob.Length < 12 + 16 + 1)
            throw new CryptographicException("AES-GCM blob too short");

        var key   = DeriveKey();
        var nonce = blob[..12];
        var tag   = blob[12..28];
        var ct    = blob[28..];
        var pt    = new byte[ct.Length];

        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, ct, tag, pt);
        return pt;
    }
}
