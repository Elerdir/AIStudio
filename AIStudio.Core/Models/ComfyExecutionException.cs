namespace AIStudio.Core.Models;

/// <summary>
/// Vyvolána pokud ComfyUI samo ohlásí chybu během exekuce workflow
/// (execution_error event přes WebSocket nebo <c>status.status_str == "error"</c>
/// v <c>/history/{id}</c>).
///
/// Cílem je odlišit tyto chyby od selhání připojení / WebSocketu — když volající
/// (např. <c>ComfyService.WaitForResultAsync</c>) zachytí WebSocket exception,
/// fallbackuje na HTTP polling. Při <see cref="ComfyExecutionException"/> ale
/// polling nemá smysl, propaguje se rovnou volajícímu jako hláška.
/// </summary>
public sealed class ComfyExecutionException : Exception
{
    public ComfyExecutionException(string message) : base(message) { }
    public ComfyExecutionException(string message, Exception inner) : base(message, inner) { }
}
