using System.Text.Json;

using Pole.Observability;

namespace Pole.Features.Events.Abstractions;

public interface IPoleEvent : ITraceContextCarrier;

public abstract record BaseEvent : IPoleEvent
{
    public DateTime OccurredOn { get; init; } = DateTime.UtcNow;
    public string TraceId { get; init; } = TraceContext.CurrentTraceId;
    public string CorrelationId { get; init; } = TraceContext.CurrentCorrelationId;
}

public static class EventExtensions
{
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
