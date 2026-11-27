// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Application.References;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.DeploymentTargets.Events;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.DeploymentTargets.Application.Bootstrap;

internal sealed record BindExistingDeploymentTargetAsDefault(
    string DeploymentTargetId
);

internal sealed record CreateFirstDeploymentTargetAsDefault(
    DeploymentTarget DeploymentTarget
);

internal union EnsureBootstrapDefaultDeploymentTargetRequest(
    BindExistingDeploymentTargetAsDefault,
    CreateFirstDeploymentTargetAsDefault);

internal sealed record BootstrapDefaultDeploymentTargetEnsured;

internal sealed record BootstrapDefaultDeploymentTargetRejected;

internal union EnsureBootstrapDefaultDeploymentTargetOutcome(
    BootstrapDefaultDeploymentTargetEnsured,
    BootstrapDefaultDeploymentTargetRejected);

internal delegate Task<EnsureBootstrapDefaultDeploymentTargetOutcome> EnsureBootstrapDefaultDeploymentTarget(
    EnsureBootstrapDefaultDeploymentTargetRequest request,
    CancellationToken cancellationToken = default);

internal sealed class MongoEnsureBootstrapDefaultDeploymentTarget(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    IMongoCollection<DeploymentTargetSettings> settings,
    EnsureNoOverlappingDeploymentTargetPublicBaseDomain ensureNoOverlappingPublicBaseDomain,
    RunMongoOutcomeTransaction<EnsureBootstrapDefaultDeploymentTargetOutcome> runMongoTransaction,
    TimeProvider timeProvider,
    StoreOutboxWrites storeOutboxWrites
)
{
    public async Task<EnsureBootstrapDefaultDeploymentTargetOutcome> InvokeAsync(
        EnsureBootstrapDefaultDeploymentTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await runMongoTransaction(
                (transaction, ct) => request switch
                {
                    BindExistingDeploymentTargetAsDefault existing => BindExistingAsync(
                        transaction,
                        existing.DeploymentTargetId,
                        ct),
                    CreateFirstDeploymentTargetAsDefault created => CreateFirstAsync(
                        transaction,
                        created.DeploymentTarget,
                        ct),
                },
                cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new BootstrapDefaultDeploymentTargetRejected();
        }
    }

    private async Task<MongoTransactionDecision<EnsureBootstrapDefaultDeploymentTargetOutcome>> BindExistingAsync(
        MongoTransactionContext<EnsureBootstrapDefaultDeploymentTargetOutcome> transaction,
        string deploymentTargetId,
        CancellationToken cancellationToken)
    {
        var target = await deploymentTargets.Find(transaction.Session, DeploymentTargetFilters.ById(deploymentTargetId))
            .FirstOrDefaultAsync(cancellationToken);
        return target is null
            ? transaction.Abort(new BootstrapDefaultDeploymentTargetRejected())
            : await BindDefaultSettingsAsync(transaction, target, cancellationToken);
    }

    private async Task<MongoTransactionDecision<EnsureBootstrapDefaultDeploymentTargetOutcome>> CreateFirstAsync(
        MongoTransactionContext<EnsureBootstrapDefaultDeploymentTargetOutcome> transaction,
        DeploymentTarget deploymentTarget,
        CancellationToken cancellationToken)
    {
        var existing = await deploymentTargets.Find(transaction.Session, DeploymentTargetFilters.NotPendingDeletion())
            .SortBy(item => item.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
            return await BindDefaultSettingsAsync(transaction, existing, cancellationToken);

        var admission = await ensureNoOverlappingPublicBaseDomain(
            deploymentTarget.Id,
            deploymentTarget.WorkloadConfiguration.Ingress?.PublicBaseDomain,
            transaction.Session,
            cancellationToken);
        if (admission is DeploymentTargetPublicBaseDomainConflict)
            return transaction.Abort(new BootstrapDefaultDeploymentTargetRejected());

        await deploymentTargets.InsertOneAsync(
            transaction.Session,
            deploymentTarget,
            cancellationToken: cancellationToken);
        await settings.InsertOneAsync(
            transaction.Session,
            DeploymentTargetSettings.Create(deploymentTarget.Id, deploymentTarget.CreatedAtUtc),
            cancellationToken: cancellationToken);
        await storeOutboxWrites(
            transaction.Session,
            [
                OutboxWrite.Create(
                    new DeploymentTargetCatalogChangedEvent(deploymentTarget.Id, deploymentTarget.Revision)),
            ],
            cancellationToken);
        return transaction.Commit(new BootstrapDefaultDeploymentTargetEnsured());
    }

    private async Task<MongoTransactionDecision<EnsureBootstrapDefaultDeploymentTargetOutcome>>
        BindDefaultSettingsAsync(
            MongoTransactionContext<EnsureBootstrapDefaultDeploymentTargetOutcome> transaction,
            DeploymentTarget target,
            CancellationToken cancellationToken)
    {
        if (target.DeletionRequestedAtUtc is not null)
            return transaction.Abort(new BootstrapDefaultDeploymentTargetRejected());

        var existing = await settings.Find(transaction.Session, DeploymentTargetSettingsFilters.Singleton())
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
            return existing.DefaultDeploymentTargetId == target.Id
                ? transaction.Commit(new BootstrapDefaultDeploymentTargetEnsured())
                : transaction.Abort(new BootstrapDefaultDeploymentTargetRejected());

        var updatedAtUtc = DeploymentTargetClock.UtcNowAfter(timeProvider, target.UpdatedAtUtc);
        var fenced = await deploymentTargets.UpdateOneAsync(
            transaction.Session,
            DeploymentTargetFilters.AtRevisionAndNotPendingDeletion(target),
            Builders<DeploymentTarget>.Update
                .Set(item => item.Revision, checked(target.Revision + 1))
                .Set(item => item.UpdatedAtUtc, updatedAtUtc),
            cancellationToken: cancellationToken);
        if (fenced.ModifiedCount != 1)
            return transaction.Abort(new BootstrapDefaultDeploymentTargetRejected());

        await settings.InsertOneAsync(
            transaction.Session,
            DeploymentTargetSettings.Create(target.Id, updatedAtUtc),
            cancellationToken: cancellationToken);
        await storeOutboxWrites(
            transaction.Session,
            [OutboxWrite.Create(new DeploymentTargetCatalogChangedEvent(target.Id, checked(target.Revision + 1)))],
            cancellationToken);
        return transaction.Commit(new BootstrapDefaultDeploymentTargetEnsured());
    }
}
