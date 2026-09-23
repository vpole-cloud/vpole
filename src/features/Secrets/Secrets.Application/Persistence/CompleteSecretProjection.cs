using MongoDB.Driver;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

public delegate Task CompleteSecretProjection(
    SecretProjection projection,
    CancellationToken ct);

internal sealed class MongoCompleteSecretProjection(IMongoCollection<SecretProjection> projections)
{
    public Task InvokeAsync(
        SecretProjection projection,
        CancellationToken ct)
    {
        if (projection.Lease is not ClaimedSecretProjection)
            throw new InvalidOperationException("Only a claimed projection can complete.");
        return projections.ReplaceOneAsync(
            SecretProjectionFilters.Identity(projection.TenantId, projection.Id) &
            SecretProjectionFilters.Revision(projection.StorageRevision) &
            SecretProjectionFilters.Lease(projection.Lease),
            projection with
            {
                Lease = new UnclaimedSecretProjection(),
                StorageRevision = checked(projection.StorageRevision + 1)
            },
            cancellationToken: ct);
    }
}
