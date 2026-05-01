namespace AIStudio.Core.Models;

/// <summary>Odkaz na jeden vygenerovaný obrázek vrácený ComfyUI API.</summary>
public record ComfyImageRef(string Filename, string Subfolder, string Type);

/// <summary>Výsledek jednoho generovacího promptu.</summary>
public record ComfyGenerationResult(
    string                      PromptId,
    IReadOnlyList<ComfyImageRef> Images,
    DateTime                    CompletedAt);
