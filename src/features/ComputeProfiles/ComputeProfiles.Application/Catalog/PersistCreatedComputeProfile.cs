// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Defaults;
using Pole.Features.ComputeProfiles.Application.Mongo;
using Pole.Features.ComputeProfiles.Application.Resolution;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.ComputeProfiles.Events;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.ComputeProfiles.Application.Catalog;

public union PersistCreatedComputeProfileOutcome(
    ComputeProfile,
    ComputeProfileRevisionConflict,
    ComputeProfileTechnicalNameConflict,
    DeploymentTargetReferenceChangeRejected);

public delegate Task<PersistCreatedComputeProfileOutcome> PersistCreatedComputeProfile(
    ComputeProfile profile,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedComputeProfile(
    IMongoCollection<ComputeProfile> computeProfiles,
    ApplyDeploymentTargetReferenceChanges applyTargetReferenceChanges,
    RunMongoOutcomeTransaction<PersistCreatedComputeProfileOutcome> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites,
    ComputeProfileHistory history
)
{
    public Task<PersistCreatedComputeProfileOutcome> InvokeAsync(
        ComputeProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return runMongoTransaction(
            async (transaction, ct) =>
            {
                var references = await ComputeProfileTargetReferences.ApplyChangesAsync(
                    applyTargetReferenceChanges,
                    profile,
                    profile.TargetMappings.Select(mapping => mapping.DeploymentTargetId),
                    [],
                    transaction.Session,
                    ct);
                if (references is DeploymentTargetReferenceChangeRejected error)
                    return transaction.Abort(error);

                if (profile.IsDefault)
                {
                    var demoted = await ComputeProfileDefaultPersistence.DemoteOtherDefaultsAsync(
                        computeProfiles,
                        history,
                        transaction.Session,
                        exceptProfileId: null,
                        profile.UpdatedAtUtc,
                        ct);
                    if (demoted is ComputeProfileRevisionConflict conflict)
                        return transaction.Abort(conflict);
                }

                try
                {
                    await computeProfiles.InsertOneAsync(transaction.Session, profile, cancellationToken: ct);
                }
                catch (MongoWriteException duplicate) when (MongoDuplicateKey.IsForIndex(
                                                                duplicate,
                                                                ComputeProfileTechnicalNames.UniqueIndexName))
                {
                    return transaction.Abort(new ComputeProfileTechnicalNameConflict(profile.TechnicalName));
                }

                await history.StoreAsync(profile, transaction.Session, ct);
                await storeOutboxWrites(
                    transaction.Session,
                    [OutboxWrite.Create(new ComputeProfileCatalogChangedEvent(profile.Id, profile.Revision))],
                    ct);
                return transaction.Commit(profile);
            },
            cancellationToken);
    }
}
