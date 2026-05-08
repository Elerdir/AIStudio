namespace AIStudio.Core.Models;

/// <summary>
/// Vyvolána po stažení, pokud SHA-256 hash staženého souboru neodpovídá
/// očekávané hodnotě. Soubor .tmp je automaticky smazán.
/// </summary>
public sealed class ChecksumMismatchException(
    string filePath,
    string expected,
    string actual)
    : Exception($"SHA-256 neshoda pro '{filePath}': očekáváno {expected}, got {actual}")
{
    public string FilePath { get; } = filePath;
    public string Expected  { get; } = expected;
    public string Actual    { get; } = actual;
}
