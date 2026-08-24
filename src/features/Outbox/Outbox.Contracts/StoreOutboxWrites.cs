using MongoDB.Driver;

namespace Pole.Features.Outbox.Contracts;

/// <summary>
/// Stores writes in the caller-owned MongoDB transaction.
/// </summary>
public delegate Task StoreOutboxWrites(
    IClientSessionHandle session,
    IReadOnlyCollection<OutboxWrite> writes,
    CancellationToken cancellationToken = default);
