using MongoDB.Driver;
using Pole.Features.SeaweedFS.Domain;
using Pole.Features.SeaweedFS.Application.Mongo;

namespace Pole.Features.SeaweedFS.Application.Services;

public delegate Task<SeaweedFSService?> LoadSeaweedFSById(
    string tenantId,
    string id,
    CancellationToken cancellationToken = default);

public delegate Task<IReadOnlyList<SeaweedFSBucket>> LoadSeaweedFSBuckets(
    string tenantId,
    string serviceId,
    CancellationToken cancellationToken = default);

internal delegate Task<SeaweedFSBucket?> LoadSeaweedFSBucket(
    string tenantId,
    string serviceId,
    string bucketId,
    CancellationToken cancellationToken);

internal sealed class MongoSeaweedFSReads(
    IMongoCollection<SeaweedFSService> services,
    IMongoCollection<SeaweedFSBucket> buckets
)
{
    public Task<SeaweedFSService?> LoadAsync(
        string tenantId,
        string id,
        CancellationToken ct) => services.Find(SeaweedFSFilters.ById(tenantId, id)).FirstOrDefaultAsync(ct)!;

    public Task<SeaweedFSBucket?> BucketAsync(
        string tenantId,
        string serviceId,
        string bucketId,
        CancellationToken ct) => buckets.Find(SeaweedFSFilters.BucketById(tenantId, serviceId, bucketId))
        .FirstOrDefaultAsync(ct)!;

    public async Task<IReadOnlyList<SeaweedFSBucket>> BucketsAsync(
        string tenantId,
        string id,
        CancellationToken ct) => await buckets.Find(SeaweedFSFilters.Buckets(tenantId, id)).ToListAsync(ct);
}
