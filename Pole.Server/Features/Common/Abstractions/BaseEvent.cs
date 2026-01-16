namespace Pole.Server.Features.Common.Abstractions;

public abstract record BaseEvent
{
    public DateTime OccurredOn { get; init; } = DateTime.UtcNow;
    public string TraceId { get; init; } = Guid.NewGuid().ToString();
}