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
            descriptor.DiagnosticsType is { } diagnostics && (!typeof(IResourceDeploymentDiagnosticsProvider).IsAssignableFrom(diagnostics) || diagnostics.IsAbstract || diagnostics.IsInterface)))
            throw new InvalidOperationException("Invalid resource deployment observer contribution.");
        if (entries.GroupBy(descriptor => descriptor.ResourceType.Value, StringComparer.Ordinal).Any(group => group.Skip(1).Any()))
            throw new InvalidOperationException("Duplicate resource deployment observer contribution.");
        _descriptors = entries.ToFrozenDictionary(descriptor => descriptor.ResourceType.Value, StringComparer.Ordinal);
    }

    public ResourceDeploymentObserverDescriptor? Find(string resourceType) => _descriptors.GetValueOrDefault(resourceType);
}
