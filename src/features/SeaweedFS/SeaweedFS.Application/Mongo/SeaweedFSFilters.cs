using MongoDB.Driver;
using Pole.Database;
using Pole.Features.SeaweedFS.Domain;

namespace Pole.Features.SeaweedFS.Application.Mongo;

internal static class SeaweedFSFilters
{
    public static FilterDefinition<SeaweedFSService> ById(string tenantId, string id) =>
        Builders<SeaweedFSService>.Filter.ForTenant(tenantId) & Builders<SeaweedFSService>.Filter.Eq(x => x.Id, id);

    public static FilterDefinition<SeaweedFSBucket> Buckets(string tenantId, string serviceId) =>
        Builders<SeaweedFSBucket>.Filter.ForTenant(tenantId) &
        Builders<SeaweedFSBucket>.Filter.Eq(x => x.SeaweedFSId, serviceId);

    public static FilterDefinition<SeaweedFSService> DueServices(DateTime now, string? afterId)
    {
        var f = Builders<SeaweedFSService>.Filter;
        var due = (f.OfType<SeaweedFSLifecycle, DeletingSeaweedFSLifecycle>(x => x.Lifecycle) & f.Lte(
            x => ((DeletingSeaweedFSLifecycle)x.Lifecycle).DeletionDeadlineUtc,
            now)) | f.OfType<SeaweedFSLifecycle, PurgingSeaweedFSLifecycle>(x => x.Lifecycle);
        return afterId is null ? due : due & f.Gt(x => x.Id, afterId);
    }

    public static FilterDefinition<SeaweedFSBucket> DueBuckets(DateTime now, string? afterId)
    {
        var f = Builders<SeaweedFSBucket>.Filter;
        var due = (f.OfType<SeaweedFSLifecycle, DeletingSeaweedFSLifecycle>(x => x.Lifecycle) & f.Lte(
            x => ((DeletingSeaweedFSLifecycle)x.Lifecycle).DeletionDeadlineUtc,
            now)) | f.OfType<SeaweedFSLifecycle, PurgingSeaweedFSLifecycle>(x => x.Lifecycle);
        return afterId is null ? due : due & f.Gt(x => x.Id, afterId);
    }

    public static FilterDefinition<SeaweedFSBucket> BucketById(
        string tenant,
        string serviceId,
        string bucketId) => Buckets(tenant, serviceId) & Builders<SeaweedFSBucket>.Filter.Eq(x => x.Id, bucketId);

    public static FilterDefinition<SeaweedFSService> AtRevision(SeaweedFSService service) =>
        ById(service.TenantId, service.Id) & Builders<SeaweedFSService>.Filter.Eq(x => x.Revision, service.Revision);

    public static FilterDefinition<SeaweedFSBucket> BucketAtRevision(SeaweedFSBucket bucket) => BucketAtRevision(
        bucket.TenantId,
        bucket.SeaweedFSId,
        bucket.Id,
        bucket.Revision);

    public static FilterDefinition<SeaweedFSBucket> BucketAtRevision(
        string tenant,
        string serviceId,
        string bucketId,
        long revision) => BucketById(tenant, serviceId, bucketId) &
        Builders<SeaweedFSBucket>.Filter.Eq(x => x.Revision, revision);

    public static FilterDefinition<SeaweedFSBucket> UnfinishedBuckets(string tenant, string serviceId) =>
        Buckets(tenant, serviceId) & Builders<SeaweedFSBucket>.Filter.Not(
            Builders<SeaweedFSBucket>.Filter.OfType<SeaweedFSLifecycle, DeletedSeaweedFSLifecycle>(x => x.Lifecycle));

    public static FilterDefinition<SeaweedFSService> PublicList(
        string tenant,
        string? search,
        string? group)
    {
        var f = Builders<SeaweedFSService>.Filter;
        var filter = f.ForTenant(tenant) & f.Eq(x => x.ReservesName, true);
        if (!string.IsNullOrWhiteSpace(search))
            filter &= f.Regex(
                x => x.Name,
                new MongoDB.Bson.BsonRegularExpression(
                    "^" + System.Text.RegularExpressions.Regex.Escape(search.Trim().ToLowerInvariant())));
        if (group is not null)
            filter &= f.Eq(x => x.ResourceGroupId, group);
        return filter;
    }

    public static FilterDefinition<SeaweedFSService> ByIds(string tenantId, IReadOnlyCollection<string> ids) =>
        Builders<SeaweedFSService>.Filter.ForTenant(tenantId) &
        Builders<SeaweedFSService>.Filter.In(item => item.Id, ids);

    public static FilterDefinition<SeaweedFSService> ActiveByName(string tenantId, string name) =>
        Builders<SeaweedFSService>.Filter.ForTenant(tenantId) &
        Builders<SeaweedFSService>.Filter.Eq(item => item.Name, name) &
        Builders<SeaweedFSService>.Filter.OfType<SeaweedFSLifecycle, ActiveSeaweedFSLifecycle>(item => item.Lifecycle);
}
