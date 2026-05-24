namespace AIStudio.Core.Models;

/// <summary>
/// Výsledek generování obrázku z chat zprávy. Vždy vrácen — i při chybě
/// (success=false + error message), aby orchestrátor nikdy nehodil výjimku.
///
/// <para>UI to umí převést na ChatMessage update: pokud Success, nastaví
/// <c>ImagePath</c> na <see cref="ImagePath"/>; jinak nastaví <c>IsImageFailed</c>
/// + obsah na <see cref="ErrorMessage"/>.</para>
/// </summary>
public sealed record ChatImageGenerationResult(
    bool     Success,
    string?  ImagePath        = null,  // absolutní cesta na disk
    string?  ImageId          = null,  // ID v ImageRepository
    string?  ModelUsed        = null,  // např. "sd_xl_base_1.0.safetensors"
    string?  EnglishPrompt    = null,  // co se nakonec pustilo do diffusionu
    string?  Reasoning        = null,  // 1-věta od intent parseru
    int      Seed             = 0,
    int      Width            = 0,
    int      Height           = 0,
    string?  ErrorMessage     = null);
