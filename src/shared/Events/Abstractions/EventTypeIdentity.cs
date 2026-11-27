// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using MassTransit;

namespace Pole.Features.Events.Abstractions;

public static class EventTypeIdentity
{
    public static string GetRequiredName(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        if (!typeof(IPoleEvent).IsAssignableFrom(eventType))
            throw new ArgumentException(
                $"Type '{DisplayName(eventType)}' does not implement {nameof(IPoleEvent)}.",
                nameof(eventType));
        if (eventType is not { IsClass: true, IsAbstract: false })
            throw new ArgumentException(
                $"Persisted event type '{DisplayName(eventType)}' must be a concrete class.",
                nameof(eventType));

        var entityName = eventType.GetCustomAttribute<EntityNameAttribute>(inherit: false)?.EntityName;
        if (string.IsNullOrWhiteSpace(entityName))
            throw new InvalidOperationException(
                $"Persisted event type '{DisplayName(eventType)}' must declare a non-empty " +
                $"[{nameof(EntityNameAttribute)}].");

        if (!IsValidIdentity(entityName))
            throw new InvalidOperationException(
                $"Persisted event type '{DisplayName(eventType)}' must declare an " +
                $"[{nameof(EntityNameAttribute)}] in the form 'namespace/vN/name', where N is a positive integer.");

        return entityName;
    }

    private static bool IsValidIdentity(string entityName)
    {
        if (!string.Equals(entityName, entityName.Trim(), StringComparison.Ordinal))
            return false;

        var segments = entityName.Split('/');
        if (segments.Length != 3 || segments.Any(string.IsNullOrWhiteSpace))
            return false;

        if (!Regex.IsMatch(segments[0], "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$") ||
            !Regex.IsMatch(segments[2], "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$"))
            return false;
        var version = segments[1];
        return version.Length > 1 && version[0] == 'v' && version[1] != '0' && int.TryParse(
            version.AsSpan(1),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value) && value > 0;
    }

    private static string DisplayName(Type eventType) => eventType.FullName ?? eventType.Name;
}
