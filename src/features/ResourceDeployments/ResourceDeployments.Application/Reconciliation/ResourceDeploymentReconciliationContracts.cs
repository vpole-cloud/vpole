using System.Collections.Frozen;
using System.Collections.Immutable;
using k8s;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceDeployments.Application.Reconciliation;

public sealed record ResourceDeploymentReconciliationContext(
    ResourceDeployment Deployment,
    RuntimeCell RuntimeCell,
    IKubernetes Kubernetes);

public interface IResourceDeploymentAdapter
{
    Task<ResourceDeploymentAdapterResult> ApplyAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken = default);

    Task<ResourceDeploymentAdapterResult> DeleteAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken = default);
}

public abstract record ResourceDeploymentAdapterResult
{
    private ResourceDeploymentAdapterResult()
    {
    }

    public sealed record Succeeded(
        ImmutableList<ResourceDeploymentEndpoint> Endpoints)
        : ResourceDeploymentAdapterResult
    {
        public static Succeeded Create(
            IEnumerable<ResourceDeploymentEndpoint>? endpoints = null) =>
            new((endpoints ?? []).ToImmutableList());
    }

    public sealed record Failed(
        string FailureCode,
        string FailureMessage,
        bool Retryable)
        : ResourceDeploymentAdapterResult
    {
        public static Failed Create(
            string failureCode,
            string failureMessage,
            bool retryable = true)
        {
            if (string.IsNullOrWhiteSpace(failureCode))
                throw new ArgumentException("Failure code is required.", nameof(failureCode));
            if (string.IsNullOrWhiteSpace(failureMessage))
                throw new ArgumentException("Failure message is required.", nameof(failureMessage));

            return new Failed(
                failureCode.Trim(),
                failureMessage.Trim(),
                retryable);
        }
    }
}

public sealed record ResourceDeploymentAdapterDescriptor(
    ResourceTypeId ResourceType,
    ResourcePlacementCapability PlacementCapability,
    Type AdapterType)
{
    public static ResourceDeploymentAdapterDescriptor Create<TAdapter>(
        ResourceTypeId resourceType,
        ResourcePlacementCapability placementCapability)
        where TAdapter : class, IResourceDeploymentAdapter =>
        new(resourceType, placementCapability, typeof(TAdapter));
}

public sealed class ResourceDeploymentAdapterCatalog(
    IEnumerable<ResourceDeploymentAdapterDescriptor> descriptors)
{
    private readonly FrozenDictionary<string, ResourceDeploymentAdapterDescriptor> _descriptors =
        Create(descriptors);

    public IReadOnlyCollection<ResourceDeploymentAdapterDescriptor> Descriptors =>
        _descriptors.Values;

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
