namespace AIStudio.Core.Models;

/// <summary>
/// Představuje jeden LoRA adapter vybraný uživatelem pro generování.
/// strength_model i strength_clip jsou obvykle stejné (1.0 = plný efekt).
/// </summary>
public sealed record LoraItem(
    string Name,
    double StrengthModel = 1.0,
    double StrengthClip  = 1.0);
