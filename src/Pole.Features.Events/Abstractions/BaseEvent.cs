using System.Text.Json;

namespace Pole.Features.Events.Abstractions;

public interface IPoleEvent;

public abstract record BaseEvent : IPoleEvent
{
    public DateTime OccurredOn { get; init; } = DateTime.UtcNow;
    public string TraceId { get; init; } = Guid.NewGuid().ToString();
}

public static class EventExtensions
{
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}