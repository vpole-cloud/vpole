using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Application.Migrations;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

[Collection(IntegrationTestCollection.Name)]
public sealed class DeploymentTargetReferenceFenceTests(MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReferenceMigration_CreatesLookupAndUniqueOwnerIndexes()
    {
        await new DeploymentTargetReferenceMigration().Up(fixture.Database);

        var indexes = await fixture.DbContext.DeploymentTargetReferences.Indexes
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
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var deletionGuard = new PausingDeletionGuard();
        var deleteHandler = CreateDeleteHandler(deletionGuard);
        var admission = CreateAdmission();
        var owner = CreateOwner();

        var deletion = deleteHandler.HandleAsync(new DeleteDeploymentTargetRequest(target.Id));
        Result referenceWrite;
        try
        {
            await deletionGuard.WaitUntilCheckedAsync().WaitAsync(TestTimeout);
            referenceWrite = await fixture.DbContext.RunInTransactionAsync(
                (session, ct) => admission.RegisterAsync(target.Id, owner, session, ct));
        }
        finally
        {
            deletionGuard.Release();
        }

        Assert.IsType<ConflictFailure>((Failure)referenceWrite);
        Assert.Equal(0, await fixture.DbContext.DeploymentTargetReferences.CountDocumentsAsync(
            Builders<DeploymentTargetReference>.Filter.Empty));
        var deleted = Assert.IsType<Success<string>>(await deletion.WaitAsync(TestTimeout));
        Assert.Equal(target.Id, deleted.Value);
        Assert.Equal(0, await fixture.DbContext.DeploymentTargets.CountDocumentsAsync(
            item => item.Id == target.Id));
    }

    [Fact]
    public async Task ReferenceTransactionStartedBeforeDeletionFence_ChangesTheDeleteSnapshot()
    {
        var target = CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var admission = CreateAdmission();
        var owner = CreateOwner();
        var referenceStaged = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReference = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var referenceWrite = fixture.DbContext.RunInTransactionAsync(async (session, ct) =>
        {
            var registered = await admission.RegisterAsync(target.Id, owner, session, ct);
            if (registered is not ISuccess)
                return registered;

            referenceStaged.TrySetResult();
            await releaseReference.Task.WaitAsync(TestTimeout, ct);
            return Result.Success();
        });

        var deleteClock = new SignalingTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        var deleteHandler = CreateDeleteHandler(
            new DeploymentTargetDeletionGuard(fixture.DbContext),
            deleteClock);
        Task<Result<string>>? deletion = null;
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

        Assert.IsType<Success>(await referenceWrite.WaitAsync(TestTimeout));
        Assert.NotNull(deletion);
        var deletionResult = await deletion.WaitAsync(TestTimeout);
        Assert.False(deletionResult.TryGetValue(out _, out var deletionFailure));
        Assert.IsType<ConflictFailure>(deletionFailure);

        var persistedTarget = await fixture.DbContext.DeploymentTargets
            .Find(item => item.Id == target.Id)
            .SingleAsync();
        Assert.Null(persistedTarget.DeletionRequestedAtUtc);
        Assert.True(persistedTarget.AcceptsWorkloads);
        Assert.Equal(target.ReferenceFence + 1, persistedTarget.ReferenceFence);
        Assert.Equal(1, await fixture.DbContext.DeploymentTargetReferences.CountDocumentsAsync(
            Builders<DeploymentTargetReference>.Filter.Empty));
    }

    [Fact]
    public async Task ReferenceAdmissionOutsideTransaction_FailsFast()
    {
        var target = CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var admission = CreateAdmission();
        using var session = await fixture.DbContext.Client.StartSessionAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => admission.RegisterAsync(target.Id, CreateOwner(), session));

        Assert.Contains("inside", exception.Message, StringComparison.Ordinal);
        var persisted = await fixture.DbContext.DeploymentTargets
            .Find(item => item.Id == target.Id)
            .SingleAsync();
        Assert.Equal(target.ReferenceFence, persisted.ReferenceFence);
    }

    [Fact]
    public async Task RegisteredReferenceThenRemoval_AllowsDeleteRetry()
    {
        var target = CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var admission = CreateAdmission();
        var owner = CreateOwner();
        var created = await fixture.DbContext.RunInTransactionAsync(
            (session, ct) => admission.RegisterAsync(target.Id, owner, session, ct));
        Assert.IsType<Success>(created);

        var deleteHandler = CreateDeleteHandler(
            new DeploymentTargetDeletionGuard(fixture.DbContext));
        var vetoed = await deleteHandler.HandleAsync(new DeleteDeploymentTargetRequest(target.Id));
        Assert.False(vetoed.TryGetValue(out _, out var vetoFailure));
        Assert.IsType<ConflictFailure>(vetoFailure);
        var pending = await fixture.DbContext.DeploymentTargets
            .Find(item => item.Id == target.Id)
            .SingleAsync();
        Assert.NotNull(pending.DeletionRequestedAtUtc);
        Assert.False(pending.AcceptsWorkloads);

        var removed = await fixture.DbContext.RunInTransactionAsync(
            (session, ct) => admission.UnregisterAsync(target.Id, owner, session, ct));
        Assert.IsType<Success>(removed);

        var retried = Assert.IsType<Success<string>>(
            await deleteHandler.HandleAsync(new DeleteDeploymentTargetRequest(target.Id)));
        Assert.Equal(target.Id, retried.Value);
        Assert.Equal(0, await fixture.DbContext.DeploymentTargets.CountDocumentsAsync(
            item => item.Id == target.Id));
        Assert.Equal(0, await fixture.DbContext.DeploymentTargetReferences.CountDocumentsAsync(
            Builders<DeploymentTargetReference>.Filter.Empty));
    }

    [Fact]
    public async Task BatchReferenceChanges_FenceTargetsAndApplyTheOwnerDiff()
    {
        var firstTarget = CreateTarget();
        var secondTarget = CreateTarget();
        var thirdTarget = CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertManyAsync(
            [firstTarget, secondTarget, thirdTarget]);
        var admission = CreateAdmission();
        var owner = CreateOwner();

        var registered = await fixture.DbContext.RunInTransactionAsync(
            (session, ct) => admission.ApplyChangesAsync(
                owner,
                [firstTarget.Id, secondTarget.Id],
                [],
                session,
                ct));
        registered.AssertSuccess();

        var changed = await fixture.DbContext.RunInTransactionAsync(
            (session, ct) => admission.ApplyChangesAsync(
                owner,
                [thirdTarget.Id],
                [firstTarget.Id],
                session,
                ct));
        changed.AssertSuccess();

        var references = await fixture.DbContext.DeploymentTargetReferences
            .Find(reference =>
                reference.OwnerType == owner.Type &&
                reference.OwnerId == owner.Id)
            .SortBy(reference => reference.DeploymentTargetId)
            .ToListAsync();
        Assert.Equal(
            new[] { secondTarget.Id, thirdTarget.Id }.Order(StringComparer.Ordinal),
            references.Select(reference => reference.DeploymentTargetId));

        var targets = await fixture.DbContext.DeploymentTargets
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
        var ineligibleTarget = CreateTarget() with { AcceptsWorkloads = false };
        await fixture.DbContext.DeploymentTargets.InsertManyAsync(
            [eligibleTarget, ineligibleTarget]);
        var admission = CreateAdmission();
        var owner = CreateOwner();

        var result = await fixture.DbContext.RunInTransactionAsync(
            (session, ct) => admission.ApplyChangesAsync(
                owner,
                [eligibleTarget.Id, ineligibleTarget.Id],
                [],
                session,
                ct));

        Assert.IsType<ConflictFailure>((Failure)result);
        Assert.Equal(0, await fixture.DbContext.DeploymentTargetReferences.CountDocumentsAsync(
            Builders<DeploymentTargetReference>.Filter.Empty));
        var persistedEligibleTarget = await fixture.DbContext.DeploymentTargets
            .Find(target => target.Id == eligibleTarget.Id)
            .SingleAsync();
        Assert.Equal(eligibleTarget.ReferenceFence, persistedEligibleTarget.ReferenceFence);
    }

    private DeleteDeploymentTargetHandler CreateDeleteHandler(
        IDeploymentTargetDeletionGuard deletionGuard,
        TimeProvider? timeProvider = null) =>
        new(
            fixture.DbContext,
            new DeploymentTargetReader(fixture.DbContext),
            deletionGuard,
            new NoOpClientCache(),
            timeProvider ?? TimeProvider.System);

    private DeploymentTargetReferenceAdmission CreateAdmission() =>
        new(fixture.DbContext, TimeProvider.System);

    private static DeploymentTargetReferenceOwner CreateOwner() =>
        new("TestResource", ObjectId.GenerateNewId().ToString());

    private static DeploymentTarget CreateTarget()
    {
        var id = ObjectId.GenerateNewId().ToString();
        const long connectionVersion = 1;
        var now = DateTime.UtcNow;
        return DeploymentTargetFactory.Create(
            "Reference fence target",
            $"kubernetes:kube-system:{id}",
            DeploymentTargetConnectionFactory.CreateInCluster(connectionVersion),
            DeploymentTargetHealth.Healthy(connectionVersion, now),
            DeploymentTargetCapabilities.Unknown(connectionVersion),
            DeploymentTargetTestData.CreateWorkloadConfiguration(),
            enabled: true,
            acceptsWorkloads: true,
            new DeploymentTargetFactory.CreateOptions(id, now));
    }

    private sealed class PausingDeletionGuard : IDeploymentTargetDeletionGuard
    {
        private readonly TaskCompletionSource _checked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilCheckedAsync() => _checked.Task;

        public void Release() => _release.TrySetResult();

        public async Task<Result> EnsureNoReferencesAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default)
        {
            _checked.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return Result.Success();
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

    private sealed class NoOpClientCache : IDeploymentTargetClientCache
    {
        public void Invalidate(string deploymentTargetId)
        {
        }
    }
}
