using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LibVLCSharp.Shared;
using Serilog;

namespace AIStudio.App.Controls;

/// <summary>
/// Vytáhne z videa první snímek a uloží ho jako PNG náhled (poster). Používá stejný
/// libVLC memory-rendering mechanismus jako <see cref="VideoPlayerControl"/> (RV32 →
/// WriteableBitmap), takže funguje bez nativního okna. Headless, best-effort —
/// když libVLC chybí nebo se snímek nepodaří získat, vrátí false a volající ukáže
/// zástupný placeholder.
///
/// <para>Generování je serializované (jeden běh naráz) — spouštět desítky libVLC
/// instancí paralelně by bylo těžké a zbytečné. Náhledy se cachují na disk, takže se
/// pro každé video počítá jen jednou.</para>
/// </summary>
public static class VideoThumbnailGenerator
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static LibVLC? _libVlc;

    /// <summary>
    /// Vytvoří náhled <paramref name="thumbPath"/> z <paramref name="videoPath"/>.
    /// Vrátí true při úspěchu. Idempotentní — když náhled už existuje, vrátí true hned.
    /// </summary>
    public static async Task<bool> TryGenerateAsync(string videoPath, string thumbPath, CancellationToken ct = default)
    {
        if (File.Exists(thumbPath)) return true;
        if (!VideoPlayerControl.IsAvailable) return false;
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath)) return false;

        await Gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => Extract(videoPath, thumbPath), ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VideoThumbnailGenerator: extrakce náhledu selhala ({Path})", videoPath);
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool Extract(string videoPath, string thumbPath)
    {
        _libVlc ??= new LibVLC("--no-audio", "--no-osd", "--quiet");

        IntPtr          buffer = IntPtr.Zero;
        WriteableBitmap? bmp   = null;
        uint            pitch = 0, lines = 0;
        var             done  = new ManualResetEventSlim(false);
        var             saved = false;

        // Delegáty drží stack frame (blokujeme v done.Wait), takže je GC nesebere.
        MediaPlayer.LibVLCVideoFormatCb formatCb = (ref IntPtr opaque, IntPtr chroma,
            ref uint width, ref uint height, ref uint pitches, ref uint lns) =>
        {
            WriteChroma(chroma, "RV32");
            pitches = width * 4u;
            lns     = height;
            pitch   = pitches;
            lines   = height;
            buffer  = Marshal.AllocHGlobal((int)(pitch * height));
            bmp     = new WriteableBitmap(new PixelSize((int)width, (int)height),
                                          new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            return 1;
        };

        MediaPlayer.LibVLCVideoLockCb lockCb = (IntPtr opaque, IntPtr planes) =>
        {
            Marshal.WriteIntPtr(planes, buffer);
            return IntPtr.Zero;
        };

        MediaPlayer.LibVLCVideoDisplayCb displayCb = (IntPtr opaque, IntPtr picture) =>
        {
            if (saved || bmp is null || buffer == IntPtr.Zero) return;
            try
            {
                using (var fb = bmp.Lock())
                {
                    var rowBytes = (uint)fb.RowBytes;
                    var copy     = Math.Min(rowBytes, pitch);
                    for (uint y = 0; y < lines; y++)
                    {
                        var src = IntPtr.Add(buffer, (int)(y * pitch));
                        var dst = IntPtr.Add(fb.Address, (int)(y * rowBytes));
                        unsafe { Buffer.MemoryCopy((void*)src, (void*)dst, rowBytes, copy); }
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);
                using (var fs = File.Create(thumbPath)) bmp.Save(fs);
                saved = true;
            }
            catch (Exception ex) { Log.Warning(ex, "VideoThumbnailGenerator: uložení náhledu selhalo"); }
            finally { done.Set(); }
        };

        MediaPlayer? player = null;
        try
        {
            player = new MediaPlayer(_libVlc);
            player.SetVideoFormatCallbacks(formatCb, null);
            player.SetVideoCallbacks(lockCb, null, displayCb);

            using var media = new Media(_libVlc, new Uri(videoPath));
            player.Play(media);

            done.Wait(TimeSpan.FromSeconds(8));   // dost na první snímek
        }
        finally
        {
            try { player?.Stop(); } catch { /* ignore */ }
            try { player?.Dispose(); } catch { /* ignore */ }
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            bmp?.Dispose();
            done.Dispose();
        }

        // Drž delegáty „naživu" až do konce (GC barrier).
        GC.KeepAlive(formatCb);
        GC.KeepAlive(lockCb);
        GC.KeepAlive(displayCb);
        return saved;
    }

    private static void WriteChroma(IntPtr chroma, string fourcc)
    {
        for (var i = 0; i < 4 && i < fourcc.Length; i++)
            Marshal.WriteByte(chroma, i, (byte)fourcc[i]);
    }
}
