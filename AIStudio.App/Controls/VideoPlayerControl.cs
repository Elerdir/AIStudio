using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Serilog;

namespace AIStudio.App.Controls;

/// <summary>
/// Inline přehrávač videa renderující snímky z libVLC do <see cref="WriteableBitmap"/>
/// (přes video callbacky), takže se zobrazuje v běžném Avalonia <c>Control</c> — žádný
/// <c>NativeControlHost</c>/VideoView, tudíž funguje i uvnitř UserControlu a je nezávislý
/// na verzi Avalonie (LibVLCSharp core nemá závislost na Avalonii).
///
/// <para><b>Graceful degradace:</b> když se nativní libVLC nepodaří inicializovat
/// (<see cref="IsAvailable"/> == false), control nic nedělá — volající má ukázat fallback
/// (statický náhled + „Přehrát externě").</para>
///
/// <para>Bezpečnost interop: delegáty callbacků se drží ve fieldech (jinak by je GC uvolnil
/// a libVLC by volal do neplatné paměti → pád). Callbacky běží na vlákně libVLC, proto
/// je veškerá práce s bitmapou pod zámkem a invalidace jde na UI vlákno.</para>
/// </summary>
public sealed class VideoPlayerControl : Control
{
    // ── libVLC dostupnost (jednorázová inicializace) ───────────────────────────

    private static readonly object InitLock = new();
    private static bool _initTried;
    private static bool _initOk;

    /// <summary>True když je nativní libVLC k dispozici a lze přehrávat.</summary>
    public static bool IsAvailable
    {
        get
        {
            lock (InitLock)
            {
                if (!_initTried)
                {
                    _initTried = true;
                    try
                    {
                        LibVLCSharp.Shared.Core.Initialize();   // najde libvlc/win-x64 vedle exe
                        _initOk = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "VideoPlayerControl: libVLC se nepodařilo inicializovat — přehrávač nebude k dispozici");
                        _initOk = false;
                    }
                }
                return _initOk;
            }
        }
    }

    // ── Source property ────────────────────────────────────────────────────────

    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<VideoPlayerControl, string?>(nameof(Source));

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    static VideoPlayerControl()
    {
        SourceProperty.Changed.AddClassHandler<VideoPlayerControl>((c, _) => c.Restart());
        AffectsRender<VideoPlayerControl>(SourceProperty);
    }

    // ── libVLC stav ────────────────────────────────────────────────────────────

    private LibVLC?      _libVlc;
    private MediaPlayer? _player;

    private readonly object        _bmpLock = new();
    private WriteableBitmap?       _bitmap;
    private IntPtr                 _buffer;
    private uint                   _pitch, _lines, _width, _height;

    // Držené delegáty — NESMÍ je sebrat GC (libVLC je volá z nativního kódu).
    private MediaPlayer.LibVLCVideoFormatCb?  _formatCb;
    private MediaPlayer.LibVLCVideoCleanupCb? _cleanupCb;
    private MediaPlayer.LibVLCVideoLockCb?    _lockCb;
    private MediaPlayer.LibVLCVideoDisplayCb? _displayCb;

    private bool _attached;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Restart();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        StopAndDispose();
    }

    // ── Spuštění / zastavení ───────────────────────────────────────────────────

    private void Restart()
    {
        StopAndDispose();
        if (!_attached) return;

        var path = Source;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        if (!IsAvailable) return;

        try
        {
            _libVlc = new LibVLC("--no-audio", "--no-osd", "--quiet");
            _player = new MediaPlayer(_libVlc);

            _formatCb  = VideoFormat;
            _cleanupCb = VideoCleanup;
            _lockCb    = LockVideo;
            _displayCb = DisplayVideo;

            _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
            _player.SetVideoCallbacks(_lockCb, null, _displayCb);

            using var media = new Media(_libVlc, new Uri(path));
            // Smyčka — generovaná videa jsou krátká, ať se přehrávají dokola.
            media.AddOption(":input-repeat=65535");
            _player.Play(media);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VideoPlayerControl: přehrávání {Path} selhalo", path);
            StopAndDispose();
        }
    }

    private void StopAndDispose()
    {
        try { _player?.Stop(); } catch { /* ignore */ }
        try { _player?.Dispose(); } catch { /* ignore */ }
        try { _libVlc?.Dispose(); } catch { /* ignore */ }
        _player = null;
        _libVlc = null;

        lock (_bmpLock)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
        }

        // Delegáty uvolníme až po Stop/Dispose (libVLC je už nevolá).
        _formatCb = null; _cleanupCb = null; _lockCb = null; _displayCb = null;
    }

    // ── libVLC callbacky (běží na vlákně libVLC) ───────────────────────────────

    private uint VideoFormat(ref IntPtr opaque, IntPtr chroma,
                             ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        try
        {
            WriteChroma(chroma, "RV32");                  // BGRA little-endian
            var pitch = width * 4u;
            pitches = pitch;
            lines   = height;

            lock (_bmpLock)
            {
                _width  = width;
                _height = height;
                _pitch  = pitch;
                _lines  = height;

                if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
                _buffer = Marshal.AllocHGlobal((int)(pitch * height));

                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(
                    new PixelSize((int)width, (int)height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            return 1;   // počet bufferů
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VideoPlayerControl: VideoFormat callback selhal");
            return 0;
        }
    }

    private void VideoCleanup(ref IntPtr opaque) { /* buffer uvolní StopAndDispose */ }

    private IntPtr LockVideo(IntPtr opaque, IntPtr planes)
    {
        // libVLC bude dekódovat do našeho bufferu.
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    private void DisplayVideo(IntPtr opaque, IntPtr picture)
    {
        try
        {
            lock (_bmpLock)
            {
                if (_bitmap is null || _buffer == IntPtr.Zero) return;
                using var fb = _bitmap.Lock();
                var rowBytes = (uint)fb.RowBytes;
                if (rowBytes == _pitch)
                {
                    unsafe { Buffer.MemoryCopy((void*)_buffer, (void*)fb.Address, rowBytes * _lines, _pitch * _lines); }
                }
                else
                {
                    // Pitch se liší od RowBytes bitmapy → kopíruj řádek po řádku.
                    var copy = Math.Min(rowBytes, _pitch);
                    for (uint y = 0; y < _lines; y++)
                    {
                        var src = IntPtr.Add(_buffer, (int)(y * _pitch));
                        var dst = IntPtr.Add(fb.Address, (int)(y * rowBytes));
                        unsafe { Buffer.MemoryCopy((void*)src, (void*)dst, rowBytes, copy); }
                    }
                }
            }
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VideoPlayerControl: DisplayVideo callback selhal");
        }
    }

    private static void WriteChroma(IntPtr chroma, string fourcc)
    {
        for (var i = 0; i < 4 && i < fourcc.Length; i++)
            Marshal.WriteByte(chroma, i, (byte)fourcc[i]);
    }

    // ── Render ─────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        lock (_bmpLock)
        {
            if (_bitmap is null) return;
            // Vykresli s zachováním poměru stran (uvnitř Bounds).
            var src  = new Rect(_bitmap.Size);
            var dest = CenteredUniform(_bitmap.Size, Bounds.Size);
            context.DrawImage(_bitmap, src, dest);
        }
    }

    private static Rect CenteredUniform(Size img, Size area)
    {
        if (img.Width <= 0 || img.Height <= 0 || area.Width <= 0 || area.Height <= 0)
            return new Rect(area);
        var scale = Math.Min(area.Width / img.Width, area.Height / img.Height);
        var w = img.Width * scale;
        var h = img.Height * scale;
        var x = (area.Width - w) / 2;
        var y = (area.Height - h) / 2;
        return new Rect(x, y, w, h);
    }
}
