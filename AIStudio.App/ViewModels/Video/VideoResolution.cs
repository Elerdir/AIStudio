namespace AIStudio.App.ViewModels.Video;

/// <summary>Preset rozlišení videa pro dropdown ve Video tabu.</summary>
public sealed record VideoResolution(string Label, int Width, int Height)
{
    public static readonly VideoResolution Landscape480 = new("480p na šířku (832×480)", 832, 480);
    public static readonly VideoResolution Landscape720 = new("720p na šířku (1280×720)", 1280, 720);
    public static readonly VideoResolution Portrait480  = new("480p na výšku (480×832)", 480, 832);
    public static readonly VideoResolution Portrait720  = new("720p na výšku (720×1280)", 720, 1280);
    public static readonly VideoResolution Square512    = new("Čtverec (512×512)", 512, 512);

    public static IReadOnlyList<VideoResolution> All { get; } =
        new[] { Landscape480, Landscape720, Portrait480, Portrait720, Square512 };

    /// <summary>Najde preset odpovídající rozměrům, jinak vrátí první (480p na šířku).</summary>
    public static VideoResolution Match(int width, int height) =>
        All.FirstOrDefault(r => r.Width == width && r.Height == height) ?? Landscape480;
}
