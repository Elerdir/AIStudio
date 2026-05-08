namespace AIStudio.Core.Models;

/// <summary>Informace o dostupné aktualizaci z GitHub Releases.</summary>
public sealed record UpdateInfo(
    string  Version,
    string  DownloadUrl,
    string  ReleaseNotes,
    DateTimeOffset PublishedAt);
