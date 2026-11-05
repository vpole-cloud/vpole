using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Projections;

public delegate Task CompleteSecretProjection(
    SecretProjection projection,
    CancellationToken ct);

internal sealed class MongoCompleteSecretProjection(
    IMongoCollection<SecretProjection> projections,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox)
{
    public Task InvokeAsync(
        SecretProjection projection,
        CancellationToken ct)
    {
        if (projection.Lease is not ClaimedSecretProjection)
            throw new InvalidOperationException("Only a claimed projection can complete.");
        return transactions.InvokeOutcomeAsync<SecretProjectionMutationOutcome>(async (tx, token) =>
            {
                var completed = projection with
                {
                    Lease = new UnclaimedSecretProjection(),
                    StorageRevision = checked(projection.StorageRevision + 1)
                };
                var updated = await projections.ReplaceOneAsync(tx.Session,
                    SecretProjectionFilters.Identity(projection.TenantId, projection.Id) &
                    SecretProjectionFilters.Revision(projection.StorageRevision) &
                    SecretProjectionFilters.Lease(projection.Lease),
                    completed,
                    cancellationToken: token);
                if (updated.ModifiedCount != 1)
                    return tx.Commit(new SecretProjectionMutationSkipped());
                await outbox(tx.Session, [OutboxWrite.Create(completed.ToChangedEvent())], token);
                return tx.Commit(new SecretProjectionMutationRecorded(completed));
            },
            ct);
    }
}
