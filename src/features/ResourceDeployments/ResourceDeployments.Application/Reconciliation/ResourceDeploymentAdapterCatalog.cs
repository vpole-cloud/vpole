using System.Collections.Frozen;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Reconciliation;

internal sealed class ResourceDeploymentAdapterCatalog(
    IEnumerable<ResourceDeploymentAdapterDescriptor> descriptors)
{
    private readonly FrozenDictionary<string, ResourceDeploymentAdapterDescriptor> _descriptors =
        Create(descriptors);

    public ResourceDeploymentAdapterDescriptor GetRequired(string resourceType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        if (_descriptors.TryGetValue(resourceType.Trim(), out var descriptor))
            return descriptor;

        throw new KeyNotFoundException(
            $"Resource deployment type '{resourceType}' is not registered.");
    }

    private static FrozenDictionary<string, ResourceDeploymentAdapterDescriptor> Create(
        IEnumerable<ResourceDeploymentAdapterDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var normalized = descriptors
            .OrderBy(descriptor => descriptor.ResourceType.Value, StringComparer.Ordinal)
            .ToArray();
        foreach (var descriptor in normalized)
        {
            if (string.IsNullOrWhiteSpace(descriptor.ResourceType.Value))
                throw new InvalidOperationException("A resource deployment descriptor has no resource type.");
            if (!typeof(IResourceDeploymentAdapter).IsAssignableFrom(descriptor.AdapterType) ||
                descriptor.AdapterType.IsAbstract ||
                descriptor.AdapterType.IsInterface)
            {
                throw new InvalidOperationException(
                    $"Resource deployment adapter '{descriptor.AdapterType.FullName}' is invalid.");
            }
        }

        var duplicate = normalized
            .GroupBy(
                descriptor => descriptor.ResourceType.Value,
                StringComparer.Ordinal)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Resource deployment type '{duplicate.Key}' is registered more than once.");
        }

        return normalized.ToFrozenDictionary(
            descriptor => descriptor.ResourceType.Value,
            StringComparer.Ordinal);
    }
}
