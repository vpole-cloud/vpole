using Pole.Features.Events.Abstractions;

namespace Pole.Database;

public sealed record OutboxWrite(
    IPoleEvent Event,
    string? PendingKey = null)
{
    public static OutboxWrite Create(
        IPoleEvent @event,
        string? pendingKey = null)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (pendingKey is not null && string.IsNullOrWhiteSpace(pendingKey))
            throw new ArgumentException("Pending key cannot be empty.", nameof(pendingKey));

        return new OutboxWrite(@event, pendingKey);
    }
}
