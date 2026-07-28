using System.Reflection;
using MassTransit;

namespace Pole.Features.Events.Abstractions;

public static class EventTypeIdentity
{
    public static string GetRequiredName(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        if (!typeof(IPoleEvent).IsAssignableFrom(eventType))
        {
            throw new ArgumentException(
                $"Type '{DisplayName(eventType)}' does not implement {nameof(IPoleEvent)}.",
                nameof(eventType));
        }
        if (eventType is not { IsClass: true, IsAbstract: false })
        {
            throw new ArgumentException(
                $"Persisted event type '{DisplayName(eventType)}' must be a concrete class.",
                nameof(eventType));
        }

        var entityName = eventType
            .GetCustomAttribute<EntityNameAttribute>(inherit: false)
            ?.EntityName;
        if (string.IsNullOrWhiteSpace(entityName))
        {
            throw new InvalidOperationException(
                $"Persisted event type '{DisplayName(eventType)}' must declare a non-empty " +
                $"[{nameof(EntityNameAttribute)}].");
        }

        return entityName.Trim();
    }

    private static string DisplayName(Type eventType) =>
        eventType.FullName ?? eventType.Name;
}
