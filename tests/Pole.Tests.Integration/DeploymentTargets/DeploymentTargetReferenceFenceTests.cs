using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Application.Catalog;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Application.References;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.DeploymentTargets.Migrations;
using Pole.Features.Outbox.Application.Writes;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

[Collection(IntegrationTestCollection.Global2)]
public sealed class DeploymentTargetReferenceFenceTests(MongoReplicaSetFixture fixture)
    : MongoIntegrationTestBase(fixture)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    public override async Task InitializeAsync() => await base.InitializeAsync();

    public override Task DisposeAsync() => base.DisposeAsync();

    [Fact]
    public async Task ReferenceMigration_CreatesLookupAndUniqueOwnerIndexes()
    {
        await new DeploymentTargetReferenceMigration().Up(Fixture.Database);

        var indexes = await Fixture.DbContext.DeploymentTargetReferences.Indexes
            .List()
            .ToListAsync();
        var names = indexes
            .Select(index => index["name"].AsString)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("IX_DeploymentTargetReferences_DeploymentTargetId", names);
        Assert.Contains("UX_DeploymentTargetReferences_Target_Owner", names);
    }

    [Fact]
    public async Task ReferenceCreationAfterDeletionFence_IsRejectedBeforeRegistryInsert()
    {
        var target = CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var deletionGuard = new PausingDeletionGuard();
        var deleteHandler = CreateDeleteHandler(deletionGuard.InvokeAsync);
        var admission = CreateAdmission();
        var owner = CreateOwner();

        var deletion = deleteHandler.HandleAsync(new DeleteDeploymentTargetRequest(target.Id));
        ApplyDeploymentTargetReferenceChangesOutcome referenceWrite;
        try
        {
            await deletionGuard.WaitUntilCheckedAsync().WaitAsync(TestTimeout);
            referenceWrite = await ApplyReferencesAsync(
                admission,
                owner,
                [target.Id],
                []);
        }
        finally
        {
            deletionGuard.Release();
        }

        Assert.IsType<DeploymentTargetReferenceChangeRejected>(referenceWrite.Value);
        Assert.Equal(0,
            await Fixture.DbContext.DeploymentTargetReferences.CountDocumentsAsync(
                Builders<DeploymentTargetReference>.Filter.Empty));
        var deleted = AssertDeleted(await deletion.WaitAsync(TestTimeout));
        Assert.Equal(target.Id, deleted.DeploymentTargetId);
        Assert.Equal(0, await Fixture.DbContext.DeploymentTargets.CountDocumentsAsync(item => item.Id == target.Id));
    }

    [Fact]
    public async Task ReferenceTransactionStartedBeforeDeletionFence_ChangesTheDeleteSnapshot()
    {
        var target = CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var admission = CreateAdmission();
        var owner = CreateOwner();
        var referenceStaged = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReference = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var referenceWrite = new MongoRunTransaction(Fixture.Client)
            .InvokeOutcomeAsync<ApplyDeploymentTargetReferenceChangesOutcome>(async (transaction, ct) =>
            {
                var registered = await admission(
                    new ApplyDeploymentTargetReferenceChangesRequest(
                        Owner: owner,
                        AddedDeploymentTargetIds: [target.Id],
                        RemovedDeploymentTargetIds: []),
                    session: transaction.Session,
                    cancellationToken: ct);
                if (registered is DeploymentTargetReferenceChangeRejected)
                    return transaction.Abort(registered);

                referenceStaged.TrySetResult();
                await releaseReference.Task.WaitAsync(TestTimeout, ct);
                return transaction.Commit(registered);
            });

        var deleteClock = new SignalingTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        var deleteHandler = CreateDeleteHandler(EnsureNoReferences, deleteClock);
        Task<DeleteDeploymentTargetOutcome>? deletion = null;
        try
        {
            await referenceStaged.Task.WaitAsync(TestTimeout);
            deletion = deleteHandler.HandleAsync(new DeleteDeploymentTargetRequest(target.Id));
            await deleteClock.WaitUntilReadAsync().WaitAsync(TestTimeout);
        }
        finally
        {
            releaseReference.TrySetResult();
        }

        Assert.IsType<DeploymentTargetReferencesChanged>((await referenceWrite.WaitAsync(TestTimeout)).Value);
        Assert.NotNull(deletion);
        var deletionResult = await deletion.WaitAsync(TestTimeout);
        Assert.True(deletionResult is DeleteDeploymentTargetError);

        var persistedTarget = await Fixture.DbContext.DeploymentTargets
            .Find(item => item.Id == target.Id)
            .SingleAsync();
        Assert.Null(persistedTarget.DeletionRequestedAtUtc);
        Assert.True(persistedTarget.AcceptsWorkloads);
        Assert.Equal(target.ReferenceFence + 1, persistedTarget.ReferenceFence);
        Assert.Equal(1,
            await Fixture.DbContext.DeploymentTargetReferences.CountDocumentsAsync(
                Builders<DeploymentTargetReference>.Filter.Empty));
    }

    [Fact]
    public async Task ReferenceAdmissionOutsideTransaction_FailsFast()
    {
        var target = CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var admission = CreateAdmission();
        using var session = await Fixture.DbContext.Client.StartSessionAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => admission(
            new ApplyDeploymentTargetReferenceChangesRequest(
                Owner: CreateOwner(),
                AddedDeploymentTargetIds: [target.Id],
                RemovedDeploymentTargetIds: []),
            session: session));

        Assert.Contains("inside", exception.Message, StringComparison.Ordinal);
        var persisted = await Fixture.DbContext.DeploymentTargets
            .Find(item => item.Id == target.Id)
            .SingleAsync();
        Assert.Equal(target.ReferenceFence, persisted.ReferenceFence);
    }

    [Fact]
    public async Task TypedReferenceChanges_RejectMalformedInputBeforeTheTransactionBoundary()
    {
        var admission = CreateAdmission();
        using var session = await Fixture.DbContext.Client.StartSessionAsync();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            admission(
                new ApplyDeploymentTargetReferenceChangesRequest(
                    Owner: CreateOwner(),
                    AddedDeploymentTargetIds: ["not-a-mongo-object-id"],
                    RemovedDeploymentTargetIds: []),
                session: session));

        Assert.Contains("ObjectId", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0,
            await Fixture.DbContext.DeploymentTargetReferences.CountDocumentsAsync(
                Builders<DeploymentTargetReference>.Filter.Empty));
    }

    [Fact]
    public async Task RegisteredReferenceThenRemoval_AllowsDeleteRetry()
    {
        var target = CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var admission = CreateAdmission();
        var owner = CreateOwner();
        var created = await ApplyReferencesAsync(admission, owner, [target.Id], []);
        Assert.IsType<DeploymentTargetReferencesChanged>((created).Value);

        var deleteHandler = CreateDeleteHandler(EnsureNoReferences);
        var vetoed = await deleteHandler.HandleAsync(new DeleteDeploymentTargetRequest(target.Id));
        Assert.True(vetoed is DeleteDeploymentTargetError);
        var pending = await Fixture.DbContext.DeploymentTargets
            .Find(item => item.Id == target.Id)
            .SingleAsync();
        Assert.NotNull(pending.DeletionRequestedAtUtc);
        Assert.False(pending.AcceptsWorkloads);

        var removed = await ApplyReferencesAsync(admission, owner, [], [target.Id]);
        Assert.IsType<DeploymentTargetReferencesChanged>((removed).Value);

        var retried = AssertDeleted(
            await deleteHandler.HandleAsync(new DeleteDeploymentTargetRequest(target.Id)));
        Assert.Equal(target.Id, retried.DeploymentTargetId);
        Assert.Equal(0, await Fixture.DbContext.DeploymentTargets.CountDocumentsAsync(item => item.Id == target.Id));
        Assert.Equal(0,
            await Fixture.DbContext.DeploymentTargetReferences.CountDocumentsAsync(
                Builders<DeploymentTargetReference>.Filter.Empty));
    }

    [Fact]
    public async Task BatchReferenceChanges_FenceTargetsAndApplyTheOwnerDiff()
    {
        var firstTarget = CreateTarget();
        var secondTarget = CreateTarget();
        var thirdTarget = CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertManyAsync(
            [firstTarget, secondTarget, thirdTarget]);
        var admission = CreateAdmission();
        var owner = CreateOwner();

        var registered = await ApplyReferencesAsync(
            admission,
            owner,
            [firstTarget.Id, secondTarget.Id],
            []);
        Assert.IsType<DeploymentTargetReferencesChanged>((registered).Value);

        var changed = await ApplyReferencesAsync(
            admission,
            owner,
            [thirdTarget.Id],
            [firstTarget.Id]);
        Assert.IsType<DeploymentTargetReferencesChanged>((changed).Value);

        var references = await Fixture.DbContext.DeploymentTargetReferences
            .Find(reference =>
                reference.OwnerType == owner.Type &&
                reference.OwnerId == owner.Id)
            .SortBy(reference => reference.DeploymentTargetId)
            .ToListAsync();
        Assert.Equal(
            new[] { secondTarget.Id, thirdTarget.Id }.Order(StringComparer.Ordinal),
            references.Select(reference => reference.DeploymentTargetId));

        var targets = await Fixture.DbContext.DeploymentTargets
            .Find(target =>
                target.Id == firstTarget.Id ||
                target.Id == secondTarget.Id ||
                target.Id == thirdTarget.Id)
            .ToListAsync();
        Assert.Equal(
            firstTarget.ReferenceFence + 2,
            targets.Single(target => target.Id == firstTarget.Id).ReferenceFence);
        Assert.Equal(
            secondTarget.ReferenceFence + 1,
            targets.Single(target => target.Id == secondTarget.Id).ReferenceFence);
        Assert.Equal(
            thirdTarget.ReferenceFence + 1,
            targets.Single(target => target.Id == thirdTarget.Id).ReferenceFence);
    }

    [Fact]
    public async Task BatchReferenceChanges_WhenOneTargetRejectsAdmission_RollsBackTheBatch()
    {
        var eligibleTarget = CreateTarget();
        var ineligibleTarget = CreateTarget() with
        {
            AcceptsWorkloads = false
        };
        await Fixture.DbContext.DeploymentTargets.InsertManyAsync(
            [eligibleTarget, ineligibleTarget]);
        var admission = CreateAdmission();
        var owner = CreateOwner();

        var result = await ApplyReferencesAsync(
            admission,
            owner,
            [eligibleTarget.Id, ineligibleTarget.Id],
            []);

        Assert.IsType<DeploymentTargetReferenceChangeRejected>(result.Value);
        Assert.Equal(0,
            await Fixture.DbContext.DeploymentTargetReferences.CountDocumentsAsync(
                Builders<DeploymentTargetReference>.Filter.Empty));
        var persistedEligibleTarget = await Fixture.DbContext.DeploymentTargets
            .Find(target => target.Id == eligibleTarget.Id)
            .SingleAsync();
        Assert.Equal(eligibleTarget.ReferenceFence, persistedEligibleTarget.ReferenceFence);
    }

    private DeleteDeploymentTargetHandler CreateDeleteHandler(
        EnsureNoDeploymentTargetReferences ensureNoDeploymentTargetReferences,
        TimeProvider? timeProvider = null) =>
        new(
            new MongoDeleteDeploymentTarget(
                Fixture.DbContext.DeploymentTargets,
                Fixture.DbContext.DeploymentTargetSettings,
                ensureNoDeploymentTargetReferences,
                new MongoRunTransaction(Fixture.Client)
                    .InvokeOutcomeAsync<DeleteDeploymentTargetPersistenceOutcome>,
                timeProvider ?? TimeProvider.System,
                new MongoRunTransaction(Fixture.Client).InvokeOutcomeAsync<PersistDeploymentTargetDeletionFenceOutcome>,
                new MongoStoreOutboxWrites(Fixture.Database.GetCollection<OutboxMessage>(
                    "OutboxMessages")).InvokeAsync).InvokeAsync,
            new NoOpClientCache().Invalidate);

    private ApplyDeploymentTargetReferenceChanges CreateAdmission() =>
        new MongoApplyDeploymentTargetReferenceChanges(
            Fixture.DbContext.DeploymentTargets,
            Fixture.DbContext.DeploymentTargetReferences,
            TimeProvider.System).InvokeAsync;

    private Task<EnsureDeploymentTargetUnreferencedOutcome> EnsureNoReferences(
        string deploymentTargetId,
        CancellationToken cancellationToken) =>
        new MongoEnsureNoDeploymentTargetReferences(
                Fixture.DbContext.DeploymentTargetReferences)
            .InvokeAsync(deploymentTargetId, cancellationToken);

    private async Task<ApplyDeploymentTargetReferenceChangesOutcome>
        ApplyReferencesAsync(
            ApplyDeploymentTargetReferenceChanges admission,
            DeploymentTargetReferenceOwner owner,
            IReadOnlyCollection<string> addedDeploymentTargetIds,
            IReadOnlyCollection<string> removedDeploymentTargetIds)
    {
        return await new MongoRunTransaction(Fixture.Client)
            .InvokeOutcomeAsync<ApplyDeploymentTargetReferenceChangesOutcome>(async (transaction, cancellationToken) =>
            {
                var result = await admission(
                    new ApplyDeploymentTargetReferenceChangesRequest(
                        Owner: owner,
                        AddedDeploymentTargetIds: addedDeploymentTargetIds,
                        RemovedDeploymentTargetIds: removedDeploymentTargetIds),
                    session: transaction.Session,
                    cancellationToken: cancellationToken);
                return result is DeploymentTargetReferencesChanged
                    ? transaction.Commit(result)
                    : transaction.Abort(result);
            });
    }

    private static DeploymentTargetDeleted AssertDeleted(
        DeleteDeploymentTargetOutcome outcome) =>
        outcome switch
        {
            DeploymentTargetDeleted deleted => deleted,
            DeleteDeploymentTargetError error => throw new Xunit.Sdk.XunitException(
                $"Expected deletion, got {error.GetType().Name}."),
            _ => throw new Xunit.Sdk.XunitException("Delete outcome returned an unsupported case.")
        };

    private static DeploymentTargetReferenceOwner CreateOwner() =>
        new("TestResource", ObjectId.GenerateNewId().ToString());

    private static DeploymentTarget CreateTarget()
    {
        var id = ObjectId.GenerateNewId().ToString();
        const long connectionVersion = 1;
        var now = DateTime.UtcNow;
        return DeploymentTargetFactory.Create("target-" + id,
            "Reference fence target",
            $"kubernetes:kube-system:{id}",
            DeploymentTargetConnectionFactory.CreateInCluster(connectionVersion),
            DeploymentTargetHealth.Healthy(connectionVersion, now),
            DeploymentTargetCapabilities.Observed(
                connectionVersion,
                now,
                "v1.35.0",
                new DeploymentTargetNamespaceCapabilities(true, true, true, true, true),
                new DeploymentTargetNetworkPolicyCapabilities(true, true, true, true, true)),
            DeploymentTargetTestData.CreateWorkloadConfiguration(),
            enabled: true,
            acceptsWorkloads: true,
            new DeploymentTargetFactory.CreateOptions(id, now)).AttestForTests();
    }

    private sealed class PausingDeletionGuard
    {
        private readonly TaskCompletionSource _checked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilCheckedAsync() => _checked.Task;

        public void Release() => _release.TrySetResult();

        public async Task<EnsureDeploymentTargetUnreferencedOutcome> InvokeAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default)
        {
            _checked.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new DeploymentTargetUnreferenced();
        }
    }

    private sealed class SignalingTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly TaskCompletionSource _read =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilReadAsync() => _read.Task;

        public override DateTimeOffset GetUtcNow()
        {
            _read.TrySetResult();
            return utcNow;
        }
    }

    private sealed class NoOpClientCache
    {
        public void Invalidate(string deploymentTargetId)
        {
        }
    }
}
