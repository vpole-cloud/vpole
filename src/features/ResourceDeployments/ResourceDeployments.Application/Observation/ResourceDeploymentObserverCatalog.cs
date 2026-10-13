using System.Collections.Frozen;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Observation;

public sealed class ResourceDeploymentObserverCatalog
{
    private readonly FrozenDictionary<string, ResourceDeploymentObserverDescriptor> _descriptors;

    public ResourceDeploymentObserverCatalog(IEnumerable<ResourceDeploymentObserverDescriptor> descriptors)
    {
        var entries = descriptors.ToArray();
        if (entries.Any(descriptor => string.IsNullOrWhiteSpace(descriptor.ResourceType.Value) ||
            !typeof(IResourceDeploymentObserver).IsAssignableFrom(descriptor.ObserverType) || descriptor.ObserverType.IsAbstract || descriptor.ObserverType.IsInterface ||
            descriptor.DiagnosticsType is { } diagnostics && (!typeof(IResourceDeploymentDiagnosticsProvider).IsAssignableFrom(diagnostics) || diagnostics.IsAbstract || diagnostics.IsInterface) ||
            (descriptor.ChangeSourceType is null) != (descriptor.BoundObserverType is null) ||
            descriptor.ChangeSourceType is { } source && (!typeof(IResourceDeploymentObservationChangeSource).IsAssignableFrom(source) || source.IsAbstract || source.IsInterface) ||
            descriptor.BoundObserverType is { } bound && (!typeof(IResourceDeploymentBoundObserver).IsAssignableFrom(bound) || bound.IsAbstract || bound.IsInterface)))
            throw new InvalidOperationException("Invalid resource deployment observer contribution.");
        if (entries.GroupBy(descriptor => descriptor.ResourceType.Value, StringComparer.Ordinal).Any(group => group.Skip(1).Any()))
            throw new InvalidOperationException("Duplicate resource deployment observer contribution.");
        _descriptors = entries.ToFrozenDictionary(descriptor => descriptor.ResourceType.Value, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<ResourceDeploymentObserverDescriptor> Contributions => _descriptors.Values;

    public ResourceDeploymentObserverDescriptor? Find(string resourceType) => _descriptors.GetValueOrDefault(resourceType);
}
