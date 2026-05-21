using CommunityToolkit.Mvvm.ComponentModel;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels.Models;

/// <summary>
/// UI wrapper kolem <see cref="HfModelInfo"/> — přidává formátované popisky
/// pro zobrazení v seznamu výsledků vyhledávání.
/// </summary>
public partial class HfModelInfoVm : ObservableObject
{
    public string   RepoId       { get; }
    public string   DisplayName  { get; }
    public string   Author       { get; }
    public long     Downloads    { get; }
    public long     Likes        { get; }
    public DateTime LastModified { get; }

    public HfModelInfoVm(HfModelInfo info)
    {
        RepoId       = info.Id;
        Downloads    = info.Downloads;
        Likes        = info.Likes;
        LastModified = info.LastModified;

        // bartowski/Magnum-v4-22b-GGUF → Author=bartowski, DisplayName=Magnum-v4-22b-GGUF
        var slash = info.Id.IndexOf('/');
        if (slash > 0)
        {
            Author      = info.Id[..slash];
            DisplayName = info.Id[(slash + 1)..];
        }
        else
        {
            Author      = string.Empty;
            DisplayName = info.Id;
        }
    }

    public string DownloadsLabel => Downloads switch
    {
        < 1_000      => $"{Downloads}",
        < 1_000_000  => $"{Downloads / 1_000.0:F1}k",
        _            => $"{Downloads / 1_000_000.0:F1}M"
    };

    public string LikesLabel => Likes < 1_000 ? $"{Likes}" : $"{Likes / 1_000.0:F1}k";
}

/// <summary>
/// UI wrapper kolem <see cref="HfFileInfo"/> + příslušnost k repu (pro download URL).
/// </summary>
public partial class HfFileInfoVm : ObservableObject
{
    public string Path     { get; }
    public string RepoId   { get; }
    public long   SizeBytes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanDownload))]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isDownloaded;

    /// <summary>True pokud varianta čeká ve frontě stahování (ještě nezačala).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanDownload))]
    private bool _isQueued;

    /// <summary>True pokud varianta běží nebo čeká — tlačítko Stáhnout je pak skryté.</summary>
    public bool IsBusy => IsDownloading || IsQueued;

    /// <summary>True pokud lze variantu stáhnout — není stažená ani ve frontě/stahuje se.</summary>
    public bool CanDownload => !IsDownloaded && !IsBusy;

    public HfFileInfoVm(HfFileInfo info, string repoId)
    {
        Path      = info.Path;
        RepoId    = repoId;
        SizeBytes = info.Size;
    }

    public string SizeLabel => SizeBytes switch
    {
        < 1_048_576     => $"{SizeBytes / 1024.0:F0} KB",
        < 1_073_741_824 => $"{SizeBytes / 1_048_576.0:F1} MB",
        _               => $"{SizeBytes / 1_073_741_824.0:F2} GB"
    };
}
