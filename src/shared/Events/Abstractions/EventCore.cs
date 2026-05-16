using System.Text.Json;

using Pole.Observability;

namespace Pole.Features.Events.Abstractions;

public interface IPoleEvent
{
    EventCore EventCore { get; }
}

public sealed record EventCore
{
    public string TenantId { get; init; } = string.Empty;
    public DateTime OccurredOn { get; init; } = DateTime.UtcNow;
    public string TraceId { get; init; } = TraceContext.CurrentTraceId;
    public string CorrelationId { get; init; } = TraceContext.CurrentCorrelationId;

    public static EventCore Create(string tenantId = "") =>
        new() { TenantId = tenantId };
}

public static class EventExtensions
{
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
