namespace AIStudio.Core.Models;

/// <summary>
/// Metadata o HuggingFace modelu z <c>api/models</c> endpointu.
/// </summary>
public record HfModelInfo(
    string   Id,                // např. "bartowski/magnum-v4-22b-GGUF"
    long     Downloads,
    long     Likes,
    DateTime LastModified
);

/// <summary>
/// Soubor v HuggingFace modelu (z <c>api/models/{id}/tree/main</c>).
/// </summary>
public record HfFileInfo(
    string Path,    // např. "magnum-v4-22b-Q4_K_M.gguf"
    long   Size     // bytes
);
