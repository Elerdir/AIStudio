using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Fáze 1 pipeline „video → LoRA": pro každé video zjistí délku, naplánuje snímky
/// (<see cref="VideoFrameSamplingPlanner"/>), vytáhne je ffmpegem
/// (<see cref="VideoFrameExtractor"/>) a popíše (<see cref="ILoraCaptionService"/>).
/// Vrací kandidáty k výběru. Trénink (fáze 2) dělá existující <see cref="ILoraTrainerService"/>.
/// </summary>
public sealed class VideoLoraPipelineService : IVideoLoraPipelineService
{
    private readonly ISettingsService    _settings;
    private readonly ILoraCaptionService _caption;
    private string? _workingDir;

    public VideoLoraPipelineService(ISettingsService settings, ILoraCaptionService caption)
    {
        _settings = settings;
        _caption  = caption;
    }

    public bool IsAnalyzing { get; private set; }

    public async Task<IReadOnlyList<CandidateFrame>> AnalyzeAsync(
        VideoLoraAnalysisRequest request,
        IProgress<VideoLoraProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IsAnalyzing = true;
        try
        {
            Report(progress, VideoLoraStage.Preparing, 0, 0, "Připravuji…");

            var ffmpeg = FfmpegVideoJoiner.ResolveFfmpeg(_settings.Settings.PythonPath);
            if (ffmpeg is null)
            {
                Report(progress, VideoLoraStage.Failed, 0, 0,
                    "Nenašel jsem ffmpeg. Nainstaluj/spusť ComfyUI (přináší ffmpeg) nebo dej ffmpeg do PATH.");
                return Array.Empty<CandidateFrame>();
            }

            // ── Plán: per video zjisti délku a navzorkuj časové značky ──────────────
            var plans = new List<(string Video, IReadOnlyList<TimeSpan> Stamps)>();
            foreach (var video in request.VideoPaths)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(video)) { Log.Warning("VideoLora: video neexistuje — {Video}", video); continue; }

                var dur = await VideoFrameExtractor.ProbeDurationAsync(ffmpeg, video, ct);
                if (dur is null || dur.Value.TotalSeconds <= 0)
                {
                    Log.Warning("VideoLora: nepodařilo se zjistit délku videa — {Video}", video);
                    continue;
                }
                var stamps = VideoFrameSamplingPlanner.Plan(dur.Value.TotalSeconds, request.Sampling);
                if (stamps.Count > 0) plans.Add((video, stamps));
            }

            var totalFrames = plans.Sum(p => p.Stamps.Count);
            if (totalFrames == 0)
            {
                Report(progress, VideoLoraStage.Failed, 0, 0, "Žádné použitelné video / snímky.");
                return Array.Empty<CandidateFrame>();
            }

            var workDir = Path.Combine(Path.GetTempPath(), $"aistudio_videolora_{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);
            _workingDir = workDir;

            // ── Extrakce snímků ─────────────────────────────────────────────────────
            var frames = new List<(string Path, string Video, TimeSpan Ts)>(totalFrames);
            var done = 0;
            foreach (var (video, stamps) in plans)
            {
                var safeStem = SafeStem(Path.GetFileNameWithoutExtension(video));
                foreach (var ts in stamps)
                {
                    ct.ThrowIfCancellationRequested();
                    var outPath = Path.Combine(workDir, $"{safeStem}_{frames.Count:D4}.jpg");
                    if (await VideoFrameExtractor.ExtractFrameAsync(ffmpeg, video, ts, outPath, ct))
                        frames.Add((outPath, video, ts));

                    done++;
                    Report(progress, VideoLoraStage.ExtractingFrames, done, totalFrames,
                        $"Vytahuji snímky… {done}/{totalFrames}");
                }
            }

            if (frames.Count == 0)
            {
                Report(progress, VideoLoraStage.Failed, 0, 0, "Žádný snímek se nepodařilo vytáhnout.");
                return Array.Empty<CandidateFrame>();
            }

            // ── Captioning (best-effort — bez popisků jde trénovat token-only) ──────
            IReadOnlyDictionary<string, string> captions = new Dictionary<string, string>();
            try
            {
                Report(progress, VideoLoraStage.Captioning, 0, frames.Count, "Generuji popisky…");
                var capProgress = new Progress<CaptionProgress>(cp =>
                    Report(progress, VideoLoraStage.Captioning, cp.Done, cp.Total, $"Popisuji… {cp.Done}/{cp.Total}"));
                captions = await _caption.CaptionAsync(
                    frames.Select(f => f.Path).ToList(), request.CaptionStyle, capProgress, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warning(ex, "VideoLora: captioning selhal — pokračuji bez popisků (token-only trénink stačí)");
            }

            var candidates = frames
                .Select(f => new CandidateFrame(
                    f.Path, f.Video, f.Ts,
                    captions.TryGetValue(f.Path, out var c) ? c : string.Empty))
                .ToList();

            Report(progress, VideoLoraStage.Completed, candidates.Count, candidates.Count,
                $"Hotovo — {candidates.Count} snímků k výběru.");
            return candidates;
        }
        catch (OperationCanceledException)
        {
            Report(progress, VideoLoraStage.Failed, 0, 0, "Analýza zrušena.");
            return Array.Empty<CandidateFrame>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VideoLora: analýza selhala");
            Report(progress, VideoLoraStage.Failed, 0, 0, "Analýza selhala: " + ex.Message);
            return Array.Empty<CandidateFrame>();
        }
        finally { IsAnalyzing = false; }
    }

    public Task CleanupAsync()
    {
        var dir = _workingDir;
        _workingDir = null;
        if (!string.IsNullOrEmpty(dir))
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { Log.Debug(ex, "VideoLora: úklid working dir selhal ({Dir})", dir); }
        }
        return Task.CompletedTask;
    }

    private static void Report(IProgress<VideoLoraProgress>? p, VideoLoraStage stage, int done, int total, string status) =>
        p?.Report(new VideoLoraProgress(stage, done, total, status));

    /// <summary>Bezpečný základ názvu souboru ze jména videa (alnum/_/-).</summary>
    private static string SafeStem(string stem)
    {
        var chars = stem.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray();
        var s = new string(chars);
        return string.IsNullOrEmpty(s) ? "frame" : s;
    }
}
