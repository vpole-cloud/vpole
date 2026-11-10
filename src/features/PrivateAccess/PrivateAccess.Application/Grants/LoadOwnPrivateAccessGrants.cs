using MongoDB.Driver;
using Pole.Features.PrivateAccess.Application.Mongo;
using Pole.Features.PrivateAccess.Domain;

namespace Pole.Features.PrivateAccess.Application.Grants;

public delegate Task<IReadOnlyList<PrivateAccessGrant>> LoadOwnPrivateAccessGrants(
    string tenantId,
    string subjectUserId,
    DateTime nowUtc,
    CancellationToken cancellationToken = default);

public delegate Task<IReadOnlyList<PrivateAccessGrant>> LoadOwnPrivateAccessResourceGrants(
    string tenantId,
    string subjectUserId,
    string resourceType,
    string resourceId,
    DateTime nowUtc,
    CancellationToken cancellationToken = default);

public delegate Task<IReadOnlyList<PrivateAccessGrant>> LoadTenantPrivateAccessGrants(
    string tenantId,
    CancellationToken cancellationToken = default);

public delegate Task<PrivateAccessGrant?> LoadPrivateAccessGrant(
    string tenantId,
    string grantId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadOwnPrivateAccessGrants(
    IMongoCollection<PrivateAccessGrant> grants
)
{
    public async Task<IReadOnlyList<PrivateAccessGrant>> InvokeAsync(
        string tenantId,
        string subjectUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var filter = PrivateAccessFilters.OwnActiveGrants(tenantId, subjectUserId, nowUtc);
        return await grants.Find(filter).SortByDescending(item => item.CreatedAtUtc).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PrivateAccessGrant>> LoadResourceAsync(
        string tenantId,
        string subjectUserId,
        string resourceType,
        string resourceId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var filter = PrivateAccessFilters.OwnActiveResourceGrants(
            tenantId,
            subjectUserId,
            resourceType,
            resourceId,
            nowUtc);
        return await grants.Find(filter).SortByDescending(item => item.CreatedAtUtc).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PrivateAccessGrant>> LoadTenantAsync(
        string tenantId,
        CancellationToken cancellationToken = default) => await grants
        .Find(PrivateAccessFilters.GrantsForTenant(tenantId))
        .SortByDescending(item => item.CreatedAtUtc)
        .ToListAsync(cancellationToken);

    public Task<PrivateAccessGrant?> LoadOneAsync(
        string tenantId,
        string grantId,
        CancellationToken cancellationToken = default) => grants.Find(PrivateAccessFilters.Grant(tenantId, grantId))
        .FirstOrDefaultAsync(cancellationToken)!;
}
