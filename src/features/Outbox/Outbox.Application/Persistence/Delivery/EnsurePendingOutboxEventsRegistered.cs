using MongoDB.Driver;

namespace Pole.Features.Outbox.Application.Persistence.Delivery;

/// <summary>Run before workers start so retained durable work always has a registered event contract.</summary>
public sealed class EnsurePendingOutboxEventsRegistered(IMongoDatabase database, OutboxEventAssemblyCatalog catalog)
{
    private readonly IMongoCollection<OutboxMessage> _messages = database.GetCollection<OutboxMessage>("OutboxMessages");

    public async Task InvokeAsync(CancellationToken cancellationToken = default)
    {
        using var names = await _messages.DistinctAsync(message => message.EventType,
            OutboxMessageFilters.Pending(), cancellationToken: cancellationToken);
        var unknown = (await names.ToListAsync(cancellationToken))
            .Where(name => !catalog.EventTypes.ContainsKey(name)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
            throw new InvalidOperationException("Pending outbox messages require unavailable event types: " +
                string.Join(", ", unknown) + ". Restore the required event contracts before processing resumes.");
    }
}
