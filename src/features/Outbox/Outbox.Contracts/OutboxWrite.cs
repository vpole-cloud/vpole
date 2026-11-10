using Pole.Features.Events.Abstractions;

namespace Pole.Features.Outbox.Contracts;

public sealed record OutboxWrite(
    IPoleEvent Event,
    string? PendingKey = null
)
{
    public static OutboxWrite Create(IPoleEvent @event, string? pendingKey = null)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (pendingKey is not null && string.IsNullOrWhiteSpace(pendingKey))
            throw new ArgumentException("A pending key cannot be whitespace.", nameof(pendingKey));

        return new OutboxWrite(@event, pendingKey);
    }
}
