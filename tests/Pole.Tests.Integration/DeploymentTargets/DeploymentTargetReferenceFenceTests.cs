using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Endpoints;
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
    public async Task ReferenceCreationAfterDeletionFence_IsRejectedBeforeReferenceInsert()
    {
        var target = CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var source = new PausingReferenceSource();
        var deleteHandler = new DeleteDeploymentTargetHandler(
            fixture.DbContext,
            [source],
            new NoOpClientCache(),
            TimeProvider.System);
        var admission = new DeploymentTargetReferenceAdmission(fixture.DbContext);
        var references = fixture.Database.GetCollection<BsonDocument>("TestDeploymentTargetReferences");

        var deletion = deleteHandler.HandleAsync(new DeleteDeploymentTargetRequest(target.Id));
        Result referenceWrite;
        try
        {
            await source.WaitUntilCheckedAsync().WaitAsync(TestTimeout);
            referenceWrite = await fixture.DbContext.RunInTransactionAsync(async (session, ct) =>
            {
                var fenced = await admission.FenceCreationAsync(target.Id, session, ct);
                if (fenced is not ISuccess)
                    return fenced;

                await references.InsertOneAsync(
                    session,
                    new BsonDocument
                    {
                        ["DeploymentTargetId"] = target.Id,
                        ["OwnerId"] = ObjectId.GenerateNewId()
                    },
                    cancellationToken: ct);
                return Result.Success();
            });
        }
        finally
        {
            source.Release();
        }

        Assert.IsType<ConflictFailure>((Failure)referenceWrite);
        Assert.Equal(0, await references.CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty));
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
        var admission = new DeploymentTargetReferenceAdmission(fixture.DbContext);
        var references = fixture.Database.GetCollection<BsonDocument>("TestDeploymentTargetReferences");
        var referenceStaged = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReference = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var referenceWrite = fixture.DbContext.RunInTransactionAsync(async (session, ct) =>
        {
            var fenced = await admission.FenceCreationAsync(target.Id, session, ct);
            if (fenced is not ISuccess)
                return fenced;

            await references.InsertOneAsync(
                session,
                new BsonDocument
                {
                    ["DeploymentTargetId"] = target.Id,
                    ["OwnerId"] = ObjectId.GenerateNewId()
                },
                cancellationToken: ct);
            referenceStaged.TrySetResult();
            await releaseReference.Task.WaitAsync(TestTimeout, ct);
            return Result.Success();
        });

        var deleteClock = new SignalingTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        var deleteHandler = new DeleteDeploymentTargetHandler(
            fixture.DbContext,
            [new AlwaysAllowReferenceSource()],
            new NoOpClientCache(),
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
        Assert.Equal(1, await references.CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty));
    }

    [Fact]
    public async Task ReferenceFenceOutsideTransaction_FailsFast()
    {
        var target = CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var admission = new DeploymentTargetReferenceAdmission(fixture.DbContext);
        using var session = await fixture.DbContext.Client.StartSessionAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => admission.FenceCreationAsync(target.Id, session));

        Assert.Contains("inside", exception.Message, StringComparison.Ordinal);
        var persisted = await fixture.DbContext.DeploymentTargets
            .Find(item => item.Id == target.Id)
            .SingleAsync();
        Assert.Equal(target.ReferenceFence, persisted.ReferenceFence);
    }

    [Fact]
    public async Task ReferenceVetoThenRemoval_AllowsDeleteRetry()
    {
        var target = CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var admission = new DeploymentTargetReferenceAdmission(fixture.DbContext);
        var references = fixture.Database.GetCollection<BsonDocument>("TestDeploymentTargetReferences");
        var ownerId = ObjectId.GenerateNewId();
        var created = await fixture.DbContext.RunInTransactionAsync(async (session, ct) =>
        {
            var fenced = await admission.FenceCreationAsync(target.Id, session, ct);
            if (fenced is not ISuccess)
                return fenced;

            await references.InsertOneAsync(
                session,
                new BsonDocument
                {
                    ["DeploymentTargetId"] = target.Id,
                    ["OwnerId"] = ownerId
                },
                cancellationToken: ct);
            return Result.Success();
        });
        Assert.IsType<Success>(created);

        var deleteHandler = new DeleteDeploymentTargetHandler(
            fixture.DbContext,
            [new CollectionReferenceSource(references)],
            new NoOpClientCache(),
            TimeProvider.System);
        var vetoed = await deleteHandler.HandleAsync(new DeleteDeploymentTargetRequest(target.Id));
        Assert.False(vetoed.TryGetValue(out _, out var vetoFailure));
        Assert.IsType<ConflictFailure>(vetoFailure);
        var pending = await fixture.DbContext.DeploymentTargets
            .Find(item => item.Id == target.Id)
            .SingleAsync();
        Assert.NotNull(pending.DeletionRequestedAtUtc);
        Assert.False(pending.AcceptsWorkloads);

        var removed = await fixture.DbContext.RunInTransactionAsync(async (session, ct) =>
        {
            var fenced = await admission.FenceRemovalAsync(target.Id, session, ct);
            if (fenced is not ISuccess)
                return fenced;

            var deletion = await references.DeleteOneAsync(
                session,
                Builders<BsonDocument>.Filter.Eq("OwnerId", ownerId),
                cancellationToken: ct);
            return deletion.DeletedCount == 1
                ? Result.Success()
                : Failures.Conflict("The test reference was not removed.");
        });
        Assert.IsType<Success>(removed);

        var retried = Assert.IsType<Success<string>>(
            await deleteHandler.HandleAsync(new DeleteDeploymentTargetRequest(target.Id)));
        Assert.Equal(target.Id, retried.Value);
        Assert.Equal(0, await fixture.DbContext.DeploymentTargets.CountDocumentsAsync(
            item => item.Id == target.Id));
        Assert.Equal(0, await references.CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty));
    }

    private static DeploymentTarget CreateTarget()
    {
        var id = ObjectId.GenerateNewId().ToString();
        const long connectionVersion = 1;
        var now = DateTime.UtcNow;
        return DeploymentTargetFactory.Create(
            "Reference fence target",
            $"kubernetes:kube-system:{id}",
            new InClusterDeploymentTargetConnection(connectionVersion),
            DeploymentTargetHealth.Healthy(connectionVersion, now),
            DeploymentTargetCapabilities.Unknown(connectionVersion),
            enabled: true,
            acceptsWorkloads: true,
            new DeploymentTargetFactory.CreateOptions(id, now));
    }

    private sealed class PausingReferenceSource : IDeploymentTargetReferenceSource
    {
        private readonly TaskCompletionSource _checked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilCheckedAsync() => _checked.Task;

        public void Release() => _release.TrySetResult();

        public async Task<Result> EnsureCanDeleteAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default)
        {
            _checked.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return Result.Success();
        }
    }

    private sealed class AlwaysAllowReferenceSource : IDeploymentTargetReferenceSource
    {
        public Task<Result> EnsureCanDeleteAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result>(Result.Success());
    }

    private sealed class CollectionReferenceSource(
        IMongoCollection<BsonDocument> references) : IDeploymentTargetReferenceSource
    {
        public async Task<Result> EnsureCanDeleteAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default)
        {
            var count = await references.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Eq("DeploymentTargetId", deploymentTargetId),
                cancellationToken: cancellationToken);
            return count == 0
                ? Result.Success()
                : Failures.Conflict("The deployment target still has test references.");
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
