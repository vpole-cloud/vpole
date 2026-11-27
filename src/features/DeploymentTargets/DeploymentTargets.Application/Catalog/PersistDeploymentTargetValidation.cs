// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.DeploymentTargets.Events;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.DeploymentTargets.Application.Catalog;

public union PersistDeploymentTargetValidationOutcome(DeploymentTarget, DeploymentTargetRevisionConflict);

public delegate Task<PersistDeploymentTargetValidationOutcome> PersistDeploymentTargetValidation(
    DeploymentTarget current,
    DeploymentTarget replacement,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistDeploymentTargetValidation(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    RunMongoOutcomeTransaction<PersistDeploymentTargetValidationOutcome> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites
)
{
    public async Task<PersistDeploymentTargetValidationOutcome> InvokeAsync(
        DeploymentTarget current,
        DeploymentTarget replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!string.Equals(current.Id, replacement.Id, StringComparison.Ordinal))
            throw new ArgumentException(
                "The current and replacement deployment targets must have the same ID.",
                nameof(replacement));

        return await runMongoTransaction(
            async (transaction, ct) =>
            {
                // Two probes can start from the same configuration revision. Compare against the
                // transaction's current observation so retrying an unchanged result stays quiet.
                var observed = await deploymentTargets.Find(
                        transaction.Session,
                        DeploymentTargetFilters.AtRevisionAndConnectionVersion(current))
                    .FirstOrDefaultAsync(ct);
                if (observed is null)
                    return transaction.Abort(new DeploymentTargetRevisionConflict(current.Id));
                var persisted = await deploymentTargets.FindOneAndUpdateAsync(
                    transaction.Session,
                    DeploymentTargetFilters.AtRevisionAndConnectionVersion(current),
                    Builders<DeploymentTarget>.Update.Combine(
                        Builders<DeploymentTarget>.Update.Set(item => item.Health, replacement.Health),
                        Builders<DeploymentTarget>.Update.Set(item => item.Capabilities, replacement.Capabilities)),
                    new FindOneAndUpdateOptions<DeploymentTarget>
                    {
                        ReturnDocument = ReturnDocument.After,
                    },
                    ct);
                if (persisted is null)
                    return transaction.Abort(new DeploymentTargetRevisionConflict(current.Id));
                if (MeaningfulObservationChanged(observed, persisted))
                    await storeOutboxWrites(
                        transaction.Session,
                        [OutboxWrite.Create(new DeploymentTargetCatalogChangedEvent(persisted.Id, persisted.Revision))],
                        ct);
                return transaction.Commit(persisted);
            },
            cancellationToken);
    }

    private static bool MeaningfulObservationChanged(DeploymentTarget before, DeploymentTarget after) =>
        before.Health.ConnectionVersion != after.Health.ConnectionVersion ||
        before.Health.Status != after.Health.Status || before.Health.FailureCode != after.Health.FailureCode ||
        before.Health.FailureMessage != after.Health.FailureMessage ||
        before.Capabilities.ConnectionVersion != after.Capabilities.ConnectionVersion ||
        before.Capabilities.IsKnown != after.Capabilities.IsKnown ||
        before.Capabilities.KubernetesVersion != after.Capabilities.KubernetesVersion ||
        before.Capabilities.NamespaceCapabilities != after.Capabilities.NamespaceCapabilities ||
        before.Capabilities.NetworkPolicyCapabilities != after.Capabilities.NetworkPolicyCapabilities;
}
