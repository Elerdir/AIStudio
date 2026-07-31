namespace AIStudio.Infrastructure.Services;

/// <summary>
/// <see cref="IProgress{T}"/>, který callback volá <b>synchronně</b> na vlákně
/// volajícího — na rozdíl od <see cref="Progress{T}"/>, který ho posílá přes
/// <see cref="SynchronizationContext"/> nebo thread pool.
///
/// <para><b>Proč:</b> pro přemostění jednoho typu progresu na druhý (bytes →
/// procenta) je asynchronní doručení dvakrát na škodu. Jednak se u thread-pool
/// postů <b>negarantuje pořadí</b>, takže po 100 % může dorazit starší 90 %
/// a progress bar zůstane viset pod stem. Jednak poslední event nemusí stihnout
/// doběhnout dřív, než volající operaci uzavře, a ztratí se úplně.</para>
///
/// <para>Marshalling do UI vlákna tím nezaniká — dělá ho konzument na druhém
/// konci (<c>ChatMessage.UpdateDownloadStatus</c> postuje na <c>Dispatcher.UIThread</c>).
/// Tenhle typ jen zajistí, že se k němu události dostanou všechny a ve správném
/// pořadí. Callback proto musí být rychlý a nesmí házet — běží uprostřed
/// stahování na jeho vlákně.</para>
/// </summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public SyncProgress(Action<T> handler) => _handler = handler;

    public void Report(T value) => _handler(value);
}
