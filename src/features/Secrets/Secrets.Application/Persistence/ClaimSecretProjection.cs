using MongoDB.Driver;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

public delegate Task<SecretProjection?> ClaimSecretProjection(
    SecretProjection desired,
    string claimId,
    DateTime now,
    CancellationToken ct);

internal sealed class MongoClaimSecretProjection(IMongoCollection<SecretProjection> projections)
{
    public async Task<SecretProjection?> InvokeAsync(
        SecretProjection desired,
        string claimId,
        DateTime now,
        CancellationToken ct)
    {
        var identity = SecretProjectionFilters.Identity(desired.TenantId, desired.Id);
        try
        {
            await projections.InsertOneAsync(desired, cancellationToken: ct);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another worker already discovered this projection.
        }
        var current = await projections.Find(identity).FirstOrDefaultAsync(ct);
        if (current.DesiredRevision > desired.DesiredRevision ||
            current.Lease is ClaimedSecretProjection lease && lease.ExpiresAtUtc > now)
            return null;
        var claimed = (desired with
        {
            AppliedVersion = current.AppliedVersion,
            StorageRevision = current.StorageRevision
        }).Claim(claimId, now);
        return await projections.FindOneAndReplaceAsync(
            identity & SecretProjectionFilters.Revision(current.StorageRevision),
            claimed,
            new FindOneAndReplaceOptions<SecretProjection> { ReturnDocument = ReturnDocument.After },
            ct);
    }
}
