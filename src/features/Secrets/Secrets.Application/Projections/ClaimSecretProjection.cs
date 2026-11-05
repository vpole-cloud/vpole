using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Projections;

public delegate Task<SecretProjection?> ClaimSecretProjection(
    SecretProjection desired,
    string claimId,
    DateTime now,
    CancellationToken ct);

internal sealed class MongoClaimSecretProjection(
    IMongoCollection<SecretProjection> projections,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox)
{
    public async Task<SecretProjection?> InvokeAsync(
        SecretProjection desired,
        string claimId,
        DateTime now,
        CancellationToken ct)
    {
        try
        {
            return await ClaimAsync(desired, claimId, now, ct);
        }
        catch (ProjectionDiscoveryRace)
        {
            // Retry discovery outside the aborted transaction; never continue its snapshot.
            return await ClaimAsync(desired, claimId, now, ct);
        }
    }

    private async Task<SecretProjection?> ClaimAsync(SecretProjection desired,
        string claimId,
        DateTime now,
        CancellationToken ct)
    {
        var identity = SecretProjectionFilters.Identity(desired.TenantId, desired.Id);
        var outcome = await transactions.InvokeOutcomeAsync<SecretProjectionMutationOutcome>(async (tx, token) =>
            {
                var current = await projections.Find(tx.Session, identity).FirstOrDefaultAsync(token);
                if (current is not null && (current.DesiredRevision > desired.DesiredRevision ||
                                            current.Lease is ClaimedSecretProjection lease && lease.ExpiresAtUtc > now))
                    return tx.Commit(new SecretProjectionMutationSkipped());
                var claimed = (desired with
                {
                    AppliedVersion = current?.AppliedVersion,
                    StorageRevision = current?.StorageRevision ?? desired.StorageRevision
                }).Claim(claimId, now);
                if (current is null)
                {
                    try
                    {
                        await projections.InsertOneAsync(tx.Session, claimed, cancellationToken: token);
                    }
                    catch (MongoWriteException exception) when (exception.WriteError.Category ==
                                                                ServerErrorCategory.DuplicateKey)
                    {
                        throw new ProjectionDiscoveryRace(exception);
                    }
                }
                else
                {
                    var updated = await projections.ReplaceOneAsync(tx.Session,
                        identity & SecretProjectionFilters.Revision(current.StorageRevision),
                        claimed,
                        cancellationToken: token);
                    if (updated.ModifiedCount != 1)
                        return tx.Abort(new SecretProjectionMutationSkipped());
                }

                await outbox(tx.Session, [OutboxWrite.Create(claimed.ToChangedEvent())], token);
                return tx.Commit(new SecretProjectionMutationRecorded(claimed));
            },
            ct);
        return outcome.Value is SecretProjectionMutationRecorded recorded ? recorded.Projection : null;
    }

    private sealed class ProjectionDiscoveryRace(MongoWriteException inner)
        : Exception("A projection was discovered concurrently.", inner);
}
