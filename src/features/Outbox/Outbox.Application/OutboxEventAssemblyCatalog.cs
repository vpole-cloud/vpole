// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Outbox.Application;

public sealed class OutboxEventAssemblyCatalog(
    IEnumerable<Assembly> assemblies
)
{
    private readonly CatalogState _state = CatalogState.Create(assemblies);

    public IReadOnlyCollection<Assembly> Assemblies => _state.Assemblies;
    public FrozenDictionary<string, Type> EventTypes => _state.EventTypes;

    public Type GetRequiredEventType(string persistedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(persistedName);
        if (EventTypes.TryGetValue(persistedName, out var eventType))
            return eventType;

        throw new KeyNotFoundException(
            $"Persisted event name '{persistedName}' is not registered. Registered names: " +
            $"{string.Join(", ", EventTypes.Keys.Order(StringComparer.Ordinal))}.");
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    private static string AssemblyName(Assembly assembly) => assembly.FullName ?? assembly.GetName().Name ?? "";

    private static string TypeName(Type type) => type.FullName ?? type.Name;

    private sealed record CatalogState(
        ImmutableArray<Assembly> Assemblies,
        FrozenDictionary<string, Type> EventTypes
    )
    {
        public static CatalogState Create(IEnumerable<Assembly> assemblies)
        {
            ArgumentNullException.ThrowIfNull(assemblies);

            var normalizedAssemblies = assemblies.Where(assembly => assembly is not null && !assembly.IsDynamic)
                .Distinct()
                .OrderBy(AssemblyName, StringComparer.Ordinal)
                .ToImmutableArray();
            var eventTypes = normalizedAssemblies.SelectMany(GetLoadableTypes)
                .Where(type =>
                    type is { IsClass: true, IsAbstract: false } && typeof(IPoleEvent).IsAssignableFrom(type))
                .Distinct()
                .OrderBy(TypeName, StringComparer.Ordinal)
                .ToArray();
            var registrations = eventTypes
                .Select(type => new EventRegistration(EventTypeIdentity.GetRequiredName(type), type))
                .ToArray();
            var duplicate = registrations.GroupBy(registration => registration.Name, StringComparer.Ordinal)
                .Where(group => group.Skip(1).Any())
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            if (duplicate is not null)
            {
                var duplicateTypes = duplicate.Select(registration => TypeName(registration.Type))
                    .Order(StringComparer.Ordinal);
                throw new InvalidOperationException(
                    $"Persisted event name '{duplicate.Key}' is declared by multiple event types: " +
                    $"{string.Join(", ", duplicateTypes)}.");
            }

            return new CatalogState(
                normalizedAssemblies,
                registrations.ToFrozenDictionary(
                    registration => registration.Name,
                    registration => registration.Type,
                    StringComparer.Ordinal));
        }
    }

    private sealed record EventRegistration(
        string Name,
        Type Type
    );
}
