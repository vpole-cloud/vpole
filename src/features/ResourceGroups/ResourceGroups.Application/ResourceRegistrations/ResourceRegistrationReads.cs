using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Workloads;

namespace Pole.Features.ResourceGroups.Application.ResourceRegistrations;

public sealed record ResourceRegistrationPageQuery(
    string TenantId,
    string ResourceGroupId,
    int PageNumber,
    int PageSize
);

public sealed record ResourceRegistrationPage(
    IReadOnlyList<ResourceRegistration> Items,
    long TotalCount
);

public delegate Task<ResourceRegistrationPage> LoadResourceRegistrationPage(
    ResourceRegistrationPageQuery query,
    CancellationToken cancellationToken = default);

public delegate Task<long> LoadResourceRegistrationCount(
    string tenantId,
    string resourceGroupId,
    CancellationToken cancellationToken = default);

internal sealed class MongoResourceRegistrationReads(
    IMongoCollection<ResourceRegistration> registrations
)
{
    public async Task<RegisteredResource?> LoadOneAsync(
        LoadRegisteredResourceRequest request,
        CancellationToken cancellationToken = default)
    {
        var registration = await registrations
            .Find(ResourceRegistrationFilters.ByIdentity(request.TenantId, request.ResourceTypeId, request.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        return registration is null
            ? null
            : new RegisteredResource(
                registration.Id,
                registration.TenantId,
                registration.ResourceGroupId,
                ResourceTypeId.Create(registration.ResourceTypeId),
                registration.ResourceId,
                registration.DisplayName,
                registration.OwningPluginId,
                registration.CreatedAtUtc,
                registration.UpdatedAtUtc);
    }

    public async Task<ResourceRegistrationPage> LoadPageAsync(
        ResourceRegistrationPageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filter = ResourceRegistrationFilters.ByResourceGroup(query.TenantId, query.ResourceGroupId);
        var totalCount = await registrations.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        var items = await registrations.Find(filter)
            .SortBy(registration => registration.ResourceTypeId)
            .ThenBy(registration => registration.ResourceId)
            .Skip(checked((query.PageNumber - 1) * query.PageSize))
            .Limit(query.PageSize)
            .ToListAsync(cancellationToken);
        return new ResourceRegistrationPage(items, totalCount);
    }

    public Task<long> CountAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default) => registrations.CountDocumentsAsync(
        ResourceRegistrationFilters.ByResourceGroup(tenantId, resourceGroupId),
        cancellationToken: cancellationToken);
}
