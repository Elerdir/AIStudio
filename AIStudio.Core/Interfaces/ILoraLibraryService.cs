namespace AIStudio.Core.Interfaces;

/// <summary>
/// Sjednocený přístup k seznamu LoRA souborů — kombinuje to, co ohlásí
/// běžící ComfyUI (přes /object_info/LoraLoader), s lokálním skenováním
/// <c>Models/lora/</c> + <c>Models/loras/</c>. ComfyUI při off-line stavu
/// vrátí prázdný list, takže lokální sken je fallback.
///
/// **Formát názvů:** používáme relativní cestu se forward slashy
/// (např. <c>anime/style.safetensors</c>), protože ComfyUI LoraLoader
/// očekává přesně takový tvar — ne jen <c>style.safetensors</c>.
/// </summary>
public interface ILoraLibraryService
{
    /// <summary>
    /// Vrátí kombinovaný (a deduplikovaný) seznam LoRA. Pokud ComfyUI běží,
    /// jeho seznam má přednost; lokální sken doplní vše, co ComfyUI nevidí.
    /// Vrácený seznam je seřazený abecedně pro stabilní UI.
    /// </summary>
    Task<IReadOnlyList<string>> ListAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Lokálně naskenuje <paramref name="modelsRoot"/> (resp. jeho podsložky
    /// <c>lora/</c> a <c>loras/</c>) a vrátí relativní názvy nalezených LoRA
    /// souborů (.safetensors / .pt / .ckpt). Pure helper bez síťových volání —
    /// dá se použít i offline.
    /// </summary>
    IReadOnlyList<string> ScanLocal(string modelsRoot);
}
