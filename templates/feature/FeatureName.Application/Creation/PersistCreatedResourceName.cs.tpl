// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.FeatureName.Application.Common;
using Pole.Features.FeatureName.Application.Mongo;
using Pole.Features.FeatureName.Domain;
using Pole.Features.FeatureName.Events;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.FeatureName.Application.Creation;

public sealed record ResourceNamePersisted;

public union PersistCreatedResourceNameOutcome(ResourceNamePersisted, ResourceNameNameAlreadyExists);

public delegate Task<PersistCreatedResourceNameOutcome> PersistCreatedResourceName(
    ResourceName resource,
    CancellationToken cancellationToken);

internal sealed class MongoPersistCreatedResourceName(
    IMongoCollection<ResourceName> resources,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<PersistCreatedResourceNameOutcome> runTransaction
)
{
    public Task<PersistCreatedResourceNameOutcome> InvokeAsync(ResourceName resource, CancellationToken ct) =>
        runTransaction(
            async (transaction, token) =>
            {
                try
                {
                    await resources.InsertOneAsync(transaction.Session, resource, cancellationToken: token);
                }
                catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(
                                                                exception,
                                                                FeatureNameIndexes.TenantName,
                                                                resources.CollectionNamespace.CollectionName))
                {
                    return transaction.Abort(new ResourceNameNameAlreadyExists(resource.Details.Name));
                }

                var changed = new ResourceNameCreatedEvent(
                    resource.Id,
                    resource.Details.Name,
                    resource.Revision,
                    EventCore.Create(resource.TenantId));
                await storeOutboxWrites(transaction.Session, [OutboxWrite.Create(changed)], token);
                return transaction.Commit(new ResourceNamePersisted());
            },
            ct);
}
