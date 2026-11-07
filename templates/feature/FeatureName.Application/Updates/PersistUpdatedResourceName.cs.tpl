using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.FeatureName.Application.Common;
using Pole.Features.FeatureName.Application.Mongo;
using Pole.Features.FeatureName.Domain;
using Pole.Features.FeatureName.Events;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.FeatureName.Application.Updates;

public sealed record UpdateResourceNameInput(
    string TenantId,
    string Id,
    long ExpectedRevision,
    ResourceNameDetails Details);

public union PersistUpdatedResourceNameOutcome(
    ResourceName,
    ResourceNameNotFound,
    ResourceNameRevisionConflict,
    ResourceNameNameAlreadyExists);

public delegate Task<PersistUpdatedResourceNameOutcome> PersistUpdatedResourceName(
    UpdateResourceNameInput request,
    CancellationToken cancellationToken);

internal sealed class MongoPersistUpdatedResourceName(
    IMongoCollection<ResourceName> resources,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<PersistUpdatedResourceNameOutcome> runTransaction,
    TimeProvider timeProvider)
{
    public Task<PersistUpdatedResourceNameOutcome> InvokeAsync(
        UpdateResourceNameInput request, CancellationToken ct) =>
        runTransaction(async (transaction, token) =>
        {
            var current = await resources.Find(transaction.Session,
                ResourceNameFilters.ById(request.TenantId, request.Id)).FirstOrDefaultAsync(token);
            if (current is null)
                return transaction.Abort(new ResourceNameNotFound(request.Id));

            if (current.Revision != request.ExpectedRevision)
                return transaction.Abort(new ResourceNameRevisionConflict(request.Id, request.ExpectedRevision));

            var updated = current.UpdateDetails(request.Details, timeProvider.GetUtcNow().UtcDateTime);
            if (ReferenceEquals(updated, current))
                return transaction.Abort(current);

            ReplaceOneResult replaced;
            try
            {
                replaced = await resources.ReplaceOneAsync(transaction.Session,
                    ResourceNameFilters.AtRevision(request.TenantId, request.Id, request.ExpectedRevision),
                    updated, cancellationToken: token);
            }
            catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(
                exception, FeatureNameIndexes.TenantName, resources.CollectionNamespace.CollectionName))
            {
                return transaction.Abort(new ResourceNameNameAlreadyExists(updated.Details.Name));
            }

            if (replaced.MatchedCount == 0)
                return transaction.Abort(new ResourceNameRevisionConflict(request.Id, request.ExpectedRevision));

            var changed = new ResourceNameUpdatedEvent(updated.Id, updated.Details.Name, updated.Revision,
                EventCore.Create(updated.TenantId));
            await storeOutboxWrites(transaction.Session, [OutboxWrite.Create(changed)], token);
            return transaction.Commit(updated);
        }, ct);
}
