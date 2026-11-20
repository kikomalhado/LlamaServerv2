using System.Threading.Channels;

namespace LlamaServer.Services;

public sealed class DocumentIndexQueue
{
    private readonly Channel<Guid> _items = Channel.CreateBounded<Guid>(new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(Guid documentId, CancellationToken cancellationToken) =>
        _items.Writer.WriteAsync(documentId, cancellationToken);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _items.Reader.ReadAllAsync(cancellationToken);
}
