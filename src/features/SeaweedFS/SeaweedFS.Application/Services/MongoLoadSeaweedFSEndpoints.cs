using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Kubernetes;
using Pole.Features.SeaweedFS.Application.Mongo;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.SeaweedFS.Domain;
using Pole.Features.ResourceConnections.Contracts;

namespace Pole.Features.SeaweedFS.Application.Services;

internal sealed class MongoLoadSeaweedFSEndpoints(
    IMongoCollection<SeaweedFSService> services
) : IResourceConnectionDestinationCapability, IResourceConnectionDestinationNameCapability
{
    public ResourceTypeId ResourceType => SeaweedFSResourceDeployment.ResourceType;

    public async Task<LoadResourceConnectionEndpointsOutcome> LoadAsync(
        LoadResourceConnectionDestinationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Connection endpoint lookup requires a transaction.");
        var resource = await services.Find(session, SeaweedFSFilters.ById(request.TenantId, request.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (resource is null)
            return new ResourceConnectionDestinationMissing();
        if (resource.Lifecycle is not ActiveSeaweedFSLifecycle)
            return new ResourceConnectionDestinationDeleting();
        return Endpoints(resource);
    }

    public async Task<IReadOnlyDictionary<string, ResourceConnectionEndpointsFound>> LoadAvailableAsync(
        string tenantId,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default)
    {
        if (resourceIds.Count == 0)
            return new Dictionary<string, ResourceConnectionEndpointsFound>();
        var resources = await services.Find(SeaweedFSFilters.ByIds(tenantId, resourceIds))
            .ToListAsync(cancellationToken);
        return resources.Where(resource => resource.Lifecycle is ActiveSeaweedFSLifecycle)
            .ToDictionary(resource => resource.Id, Endpoints, StringComparer.Ordinal);
    }

    public string Kind => "seaweedfs";

    public async Task<string?> ResolveResourceIdAsync(
        string tenantId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var matches = await services.Find(SeaweedFSFilters.ActiveByName(tenantId, name))
            .Limit(2)
            .ToListAsync(cancellationToken);
        return ResourceConnectionNameResolution.ResolveUniqueResourceId(
            matches,
            resource => resource.Lifecycle is ActiveSeaweedFSLifecycle,
            resource => resource.Id);
    }

    private static ResourceConnectionEndpointsFound Endpoints(SeaweedFSService resource) => new(
        ImmutableList.Create(
            new ResourceConnectionEndpoint(
                "s3",
                SeaweedFSNames.S3Service(resource.Id),
                NetworkPort.Create(8333),
                NetworkPort.Create(8333))
            {
                DisplayName = "S3",
                Protocol = "http",
            }));
}

public static class SeaweedFSConnectionsRegistration
{
    public static IServiceCollection AddSeaweedFSConnections(this IServiceCollection services)
    {
        services.AddScoped<MongoLoadSeaweedFSEndpoints>();
        services.AddScoped<IResourceConnectionDestinationCapability>(provider =>
            provider.GetRequiredService<MongoLoadSeaweedFSEndpoints>());
        services.AddScoped<IResourceConnectionDestinationNameCapability>(provider =>
            provider.GetRequiredService<MongoLoadSeaweedFSEndpoints>());
        return services;
    }
}
