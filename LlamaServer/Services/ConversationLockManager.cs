using System.Collections.Concurrent;

namespace LlamaServer.Services;

public sealed class ConversationLockManager
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public SemaphoreSlim Get(Guid conversationId) =>
        _locks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
}
