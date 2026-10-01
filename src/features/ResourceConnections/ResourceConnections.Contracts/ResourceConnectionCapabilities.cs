using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Contracts;

/// <summary>A provider opts its workload pods into connection source selection.</summary>
public sealed record ResourceConnectionSourceCapability(ResourceTypeId ResourceType);

public sealed record ResourceConnectionEndpoint(
    string Id,
    string ServiceName,
    NetworkPort ServicePort,
    NetworkPort TargetPort);

public sealed record ResourceConnectionEndpointsFound(
    ImmutableList<ResourceConnectionEndpoint> Endpoints);

public sealed record ResourceConnectionDestinationMissing;

public sealed record ResourceConnectionDestinationDeleting;

public union LoadResourceConnectionEndpointsOutcome(
    ResourceConnectionEndpointsFound,
    ResourceConnectionDestinationMissing,
    ResourceConnectionDestinationDeleting);

/// <summary>
/// A destination provider reads its desired endpoints inside the caller's Mongo transaction.
/// The caller owns the resource registration write fence.
/// </summary>
public interface IResourceConnectionDestinationCapability
{
    ResourceTypeId ResourceType { get; }

    Task<LoadResourceConnectionEndpointsOutcome> LoadAsync(
        string tenantId,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

/// <summary>A provider resolves an immutable resource name within an already validated tenant.</summary>
public interface IResourceConnectionDestinationNameCapability
{
    string Kind { get; }

    ResourceTypeId ResourceType { get; }

    Task<string?> ResolveResourceIdAsync(
        string tenantId,
        string name,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves only a single matching provider resource; duplicates must never select arbitrarily.</summary>
public static class ResourceConnectionNameResolution
{
    public static string? ResolveUniqueResourceId<TResource>(
        IReadOnlyList<TResource> matches,
        Func<TResource, bool> isEligible,
        Func<TResource, string> getId)
    {
        if (matches.Count != 1)
            return null;

        var resource = matches[0];
        return isEligible(resource) ? getId(resource) : null;
    }
}
