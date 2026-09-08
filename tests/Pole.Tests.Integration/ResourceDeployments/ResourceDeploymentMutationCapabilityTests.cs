using MongoDB.Bson;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Application.Persistence.Reads;
using Pole.Features.ResourceDeployments.Application.Persistence.Reconciliation;
using Pole.Features.ResourceDeployments.Application.Persistence.Requests;
using Pole.Features.ResourceGroups.Application.Persistence;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

[Collection(IntegrationTestCollection.Name)]
public sealed class ResourceDeploymentMutationCapabilityTests(
    MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    private ServiceProvider? serviceProvider;
    private IServiceScope? serviceScope;
    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string OtherTenantId = "507f1f77bcf86cd799439012";
    private const string ResourceId = "507f1f77bcf86cd799439013";
    private const string ResourceGroupId = "507f1f77bcf86cd799439014";
    private const string ScopeId = "507f1f77bcf86cd799439015";
    private static readonly ResourceTypeId TestResourceType =
        ResourceTypeId.Create("pole/test-resource");
    private const string RuntimeCellId = "507f1f77bcf86cd799439016";
    private const string TargetId = "507f1f77bcf86cd799439017";
    private const string DeploymentId = "507f1f77bcf86cd799439018";
    private static readonly DateTime CreatedAtUtc =
        new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MutationAtUtc =
        CreatedAtUtc.AddMinutes(2);

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        serviceProvider = TestServiceProviderFactory.Create(fixture);
        serviceScope = serviceProvider.CreateScope();
    }

    public Task DisposeAsync()
    {
        serviceScope?.Dispose();
        serviceProvider?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RequestApply_AdvancesGeneration_PreservesPlacement_AndQueuesCurrentRequest()
    {
        var (runtimeCell, deployment) = CreateAggregate(runtimeCellReady: true);
        await InsertAsync(runtimeCell, deployment);
        var mutations = CreateMutations();

        var result = await RunTypedTransactionAsync((session, cancellationToken) =>
            mutations.Apply(CreateCommand(), session, cancellationToken));

        var summary = result.AssertSuccess();
        Assert.Equal(2, summary.DesiredGeneration);
        var updated = await LoadDeploymentAsync();
        Assert.Equal(deployment.ResolvedPlacement, updated.ResolvedPlacement);
        var pending = Assert.IsType<PendingResourceDeploymentState>(updated.State);
        Assert.Equal(ResourceDeploymentOperation.Apply, pending.Operation);
        var request = await fixture.DbContext.OutboxMessages
            .Find(message =>
                message.PendingKey == ResourceDeploymentPendingKey.Apply(
                    DeploymentId,
                    2))
            .SingleAsync();
        Assert.Equal("pole.resource-deployments/v1/resource-deployment-reconciliation-requested", request.EventType);
    }

    [Fact]
    public async Task RequestApply_WaitsWithoutOutbox_WhenRuntimeCellIsNotReady()
    {
        var (runtimeCell, deployment) = CreateAggregate(runtimeCellReady: false);
        await InsertAsync(runtimeCell, deployment);
        var mutations = CreateMutations();

        var result = await RunTypedTransactionAsync((session, cancellationToken) =>
            mutations.Apply(CreateCommand(), session, cancellationToken));

        _ = result.AssertSuccess();
        var updated = await LoadDeploymentAsync();
        var waiting = Assert.IsType<WaitingForRuntimeCellResourceDeploymentState>(
            updated.State);
        Assert.Equal(2, waiting.Generation);
        Assert.Equal(
            0,
            await fixture.DbContext.OutboxMessages.CountDocumentsAsync(
                FilterDefinition<Pole.Features.Outbox.Application.Model.OutboxMessage>.Empty));
    }

    [Fact]
    public async Task RequestDeletion_AdvancesGeneration_AndQueuesDeleteRequest()
    {
        var (runtimeCell, deployment) = CreateAggregate(runtimeCellReady: true);
        await InsertAsync(runtimeCell, deployment);
        var mutations = CreateMutations();

        var result = await RunTypedTransactionAsync((session, cancellationToken) =>
            mutations.Deletion(CreateCommand(), session, cancellationToken));

        var summary = result.AssertSuccess();
        Assert.Equal(2, summary.DesiredGeneration);
        var updated = await LoadDeploymentAsync();
        Assert.Equal(ResourceDeploymentOperation.Delete, updated.DesiredOperation);
        var pending = Assert.IsType<PendingResourceDeploymentState>(updated.State);
        Assert.Equal(ResourceDeploymentOperation.Delete, pending.Operation);
        var request = await fixture.DbContext.OutboxMessages
            .Find(message =>
                message.PendingKey == ResourceDeploymentPendingKey.Delete(
                    DeploymentId,
                    2))
            .SingleAsync();
        Assert.Equal("pole.resource-deployments/v1/resource-deployment-deletion-requested", request.EventType);
    }

    [Fact]
    public async Task RequestExactRetry_FencesRevisionAndOperation_AndQueuesCurrentRequest()
    {
        const string claimId = "507f1f77bcf86cd799439019";
        var (runtimeCell, deployment) = CreateAggregate(runtimeCellReady: true);
        var claimed = deployment.Start(
            expectedGeneration: 1,
            ResourceDeploymentOperation.Apply,
            claimId,
            CreatedAtUtc.AddSeconds(30),
            CreatedAtUtc.AddMinutes(1));
        var failed = claimed.Fail(
            expectedGeneration: 1,
            ResourceDeploymentOperation.Apply,
            claimId,
            "ProviderUnavailable",
            "The provider was unavailable.",
            retryable: true,
            CreatedAtUtc.AddMinutes(1),
            CreatedAtUtc.AddMinutes(1));
        await InsertAsync(runtimeCell, failed);
        var mutations = CreateMutations();

        var mismatch = await RunTypedTransactionAsync((session, cancellationToken) =>
            mutations.Retry(
                    new ResourceDeploymentExactRetryRequest(
                        TenantId,
                        TestResourceType,
                        ResourceId,
                        DeploymentId,
                        failed.Revision,
                        ResourceDeploymentRequestedOperation.Delete),
                    session,
                    cancellationToken));

        Assert.IsType<ResourceDeploymentRetryUnavailable>(mismatch.AssertFailure().Value);

        var result = await RunTypedTransactionAsync((session, cancellationToken) =>
            mutations.Retry(
                    new ResourceDeploymentExactRetryRequest(
                        TenantId,
                        TestResourceType,
                        ResourceId,
                        DeploymentId,
                        failed.Revision,
                        ResourceDeploymentRequestedOperation.Apply),
                    session,
                    cancellationToken));

        var summary = result.AssertSuccess();
        Assert.Equal(1, summary.DesiredGeneration);
        Assert.Equal(failed.Revision + 1, summary.DeploymentRevision);
        var retried = await LoadDeploymentAsync();
        var pending = Assert.IsType<PendingResourceDeploymentState>(retried.State);
        Assert.Equal(ResourceDeploymentOperation.Apply, pending.Operation);
        Assert.Equal(1, pending.AttemptCount);
        var request = await fixture.DbContext.OutboxMessages
            .Find(message =>
                message.PendingKey == ResourceDeploymentPendingKey.Apply(
                    DeploymentId,
                    1))
            .SingleAsync();
        Assert.Equal("pole.resource-deployments/v1/resource-deployment-reconciliation-requested", request.EventType);
    }

    [Fact]
    public async Task Mutation_IsTenantScoped()
    {
        var (runtimeCell, deployment) = CreateAggregate(runtimeCellReady: true);
        await InsertAsync(runtimeCell, deployment);
        var mutations = CreateMutations();

        var result = await RunTypedTransactionAsync((session, cancellationToken) =>
            mutations.Apply(
                CreateCommand() with { TenantId = OtherTenantId },
                session,
                cancellationToken));

        Assert.IsType<ResourceDeploymentMutationNotFound>(result.AssertFailure().Value);
        var saved = await fixture.DbContext.ResourceDeployments
            .Find(item => item.Id == DeploymentId)
            .SingleAsync();
        Assert.Equal(1, saved.DesiredGeneration);
    }

    [Fact]
    public async Task Mutation_ReturnsProviderOwnedNotFoundError()
    {
        var mutations = CreateMutations(new RecordingComputeProfileFence());
        using var session = await fixture.DbContext.Client.StartSessionAsync();
        session.StartTransaction();
        try
        {
            var result = await mutations.Apply(
                CreateCommand(),
                session);

            Assert.False(result.TryGetValue(out _, out var error));
            var notFound = Assert.IsType<ResourceDeploymentMutationNotFound>(
                error.Value);
            Assert.Equal(ResourceId, notFound.ResourceId);
        }
        finally
        {
            if (session.IsInTransaction)
                await session.AbortTransactionAsync();
        }
    }

    [Fact]
    public async Task RequestApply_WithReplacement_FencesAndAdoptsResolvedComputeSnapshot()
    {
        var (runtimeCell, deployment) = CreateAggregate(runtimeCellReady: true);
        await InsertAsync(runtimeCell, deployment);
        var fence = new RecordingComputeProfileFence();
        var mutations = CreateMutations(fence);
        var replacement = ResourceDeploymentTestData.ContractCompute(
            TargetId,
            revision: 2,
            requestsCpuMillis: 500,
            requestsMemoryMi: 512);

        var result = await RunTypedTransactionAsync((session, cancellationToken) =>
            mutations.Apply(
                CreateCommand() with { ReplacementCompute = replacement },
                session,
                cancellationToken));

        _ = result.AssertSuccess();
        var updated = await LoadDeploymentAsync();
        Assert.Equal(1, fence.CallCount);
        Assert.Equal(2, updated.ResolvedPlacement.Compute.ComputeProfileRevision);
        Assert.Equal(500, updated.ResolvedPlacement.Compute.Requests.CpuMillis);
        Assert.Equal(512, updated.ResolvedPlacement.Compute.Requests.MemoryMi);
    }

    [Fact]
    public async Task RequestApply_WithStaleComputeExpectation_DoesNotMutateOrFence()
    {
        var (runtimeCell, deployment) = CreateAggregate(runtimeCellReady: true);
        await InsertAsync(runtimeCell, deployment);
        var fence = new RecordingComputeProfileFence();
        var mutations = CreateMutations(fence);

        var result = await RunTypedTransactionAsync((session, cancellationToken) =>
            mutations.Apply(
                    CreateCommand() with
                    {
                        ExpectedCompute = new ComputeProfileReference(
                            ResourceDeploymentTestData.ComputeProfileId,
                            ComputeProfileRevision: 2),
                        ReplacementCompute =
                            ResourceDeploymentTestData.ContractCompute(
                                TargetId,
                                revision: 2)
                    },
                    session,
                    cancellationToken));

        Assert.IsType<ResourceDeploymentComputeConfigurationChanged>(
            result.AssertFailure().Value);
        Assert.Equal(0, fence.CallCount);
        var saved = await fixture.DbContext.ResourceDeployments
            .Find(item => item.Id == DeploymentId)
            .SingleAsync();
        Assert.Equal(1, saved.DesiredGeneration);
        Assert.Equal(1, saved.ResolvedPlacement.Compute.ComputeProfileRevision);
    }

    [Fact]
    public async Task ComputeLookup_ReturnsThePersistedQuotaAndVersionSnapshot()
    {
        var (runtimeCell, deployment) = CreateAggregate(runtimeCellReady: true);
        await InsertAsync(runtimeCell, deployment);
        var lookup = new MongoLoadCurrentResourceDeploymentCompute(
            fixture.DbContext.ResourceDeployments);

        var result = await lookup.InvokeAsync(
            TenantId,
            TestResourceType,
            ResourceId);

        Assert.True(result.TryGetValue(out var compute, out var error), error.ToString());
        Assert.Equal(TargetId, compute.DeploymentTargetId);
        Assert.Equal(
            ResourceDeploymentTestData.ComputeProfileId,
            compute.Compute.Profile.ComputeProfileId);
        Assert.Equal(1, compute.Compute.Profile.ComputeProfileRevision);
        Assert.Equal(250, compute.Compute.Requests.CpuMillis);
        Assert.Equal(256, compute.Compute.Requests.MemoryMi);
        Assert.Empty(compute.Compute.ExtendedResources);
    }

    [Fact]
    public async Task CallerFailure_AbortsDeploymentAndOutboxWrites()
    {
        var (runtimeCell, deployment) = CreateAggregate(runtimeCellReady: true);
        await InsertAsync(runtimeCell, deployment);
        var mutations = CreateMutations();

        var result = await fixture.DbContext
            .RunOutcomeInTransactionAsync<PlacementMutationRejected>(
                async (transaction, cancellationToken) =>
                {
                    var mutation = await mutations.Apply(
                        CreateCommand(),
                        transaction.Session,
                        cancellationToken);
                    _ = mutation.AssertSuccess();
                    return transaction.Abort(
                        new PlacementMutationRejected(
                            "The logical resource write failed."));
                });

        Assert.Equal("The logical resource write failed.", result.Message);
        var saved = await fixture.DbContext.ResourceDeployments
            .Find(item => item.Id == DeploymentId)
            .SingleAsync();
        Assert.Equal(1, saved.DesiredGeneration);
        Assert.Equal(
            0,
            await fixture.DbContext.OutboxMessages.CountDocumentsAsync(
                FilterDefinition<Pole.Features.Outbox.Application.Model.OutboxMessage>.Empty));
    }

    [Fact]
    public async Task RequestSetApply_UsesBatchPlacementAndComputeFencesOnce()
    {
        const string secondRuntimeCellId = "507f1f77bcf86cd79943901a";
        const string secondTargetId = "507f1f77bcf86cd79943901b";
        const string secondDeploymentId = "507f1f77bcf86cd79943901c";
        var first = CreateAggregate(runtimeCellReady: true);
        var second = CreateAggregate(
            secondRuntimeCellId,
            secondTargetId,
            secondDeploymentId,
            runtimeCellReady: true);
        await fixture.DbContext.RuntimeCells.InsertManyAsync(
            [first.RuntimeCell, second.RuntimeCell]);
        await fixture.DbContext.ResourceDeployments.InsertManyAsync(
            [first.Deployment, second.Deployment]);
        var placementLookup = CreateRecordingPlacementLookup();
        var fence = new RecordingComputeProfileFence();
        var mutations = CreateMutations(fence, placementLookup);

        var result = await RunTypedTransactionAsync(
            (session, cancellationToken) => mutations.SetApply(
                new ResourceDeploymentSetApplyRequest(
                    TenantId,
                    TestResourceType,
                    ResourceId,
                    ResourceRevision: 2,
                    [
                        new ResourceDeploymentSetApplyItem(
                            DeploymentId,
                            first.Deployment.Revision,
                            new ComputeProfileReference(
                                ResourceDeploymentTestData.ComputeProfileId,
                                1),
                            ReplacementCompute: ResourceDeploymentTestData.ContractCompute(
                                TargetId,
                                revision: 2)),
                        new ResourceDeploymentSetApplyItem(
                            secondDeploymentId,
                            second.Deployment.Revision,
                            new ComputeProfileReference(
                                ResourceDeploymentTestData.ComputeProfileId,
                                1),
                            ReplacementCompute: ResourceDeploymentTestData.ContractCompute(
                                secondTargetId,
                                revision: 2))
                    ]),
                session,
                cancellationToken));

        var summaries = result.AssertSuccess();
        Assert.Equal([DeploymentId, secondDeploymentId], summaries.Select(item => item.DeploymentId));
        Assert.Equal(1, placementLookup.FindManyCallCount);
        Assert.Equal(0, placementLookup.FindCallCount);
        Assert.Equal(1, fence.FenceManyCallCount);
        Assert.Equal(2, fence.LastBatchCount);
        Assert.Equal(0, fence.CallCount);
        Assert.Equal(
            2L,
            await fixture.DbContext.CountOutboxMessagesAsync(
                "pole.resource-deployments/v1/resource-deployment-reconciliation-requested"));
    }

    [Fact]
    public async Task RequestSetDeletion_BatchLoadsAndQueuesEachExactDeploymentInOrder()
    {
        const string secondRuntimeCellId = "507f1f77bcf86cd79943901d";
        const string secondTargetId = "507f1f77bcf86cd79943901e";
        const string secondDeploymentId = "507f1f77bcf86cd79943901f";
        var first = CreateAggregate(runtimeCellReady: true);
        var second = CreateAggregate(
            secondRuntimeCellId,
            secondTargetId,
            secondDeploymentId,
            runtimeCellReady: true);
        await fixture.DbContext.RuntimeCells.InsertManyAsync(
            [first.RuntimeCell, second.RuntimeCell]);
        await fixture.DbContext.ResourceDeployments.InsertManyAsync(
            [first.Deployment, second.Deployment]);
        var mutations = CreateMutations();

        var result = await RunTypedTransactionAsync(
            (session, cancellationToken) => mutations.SetDeletion(
                new ResourceDeploymentSetDeletionRequest(
                    TenantId,
                    TestResourceType,
                    ResourceId,
                    ResourceRevision: 2,
                    [
                        new ResourceDeploymentSetDeletionItem(
                            secondDeploymentId,
                            second.Deployment.Revision,
                            new ComputeProfileReference(
                                ResourceDeploymentTestData.ComputeProfileId,
                                1)),
                        new ResourceDeploymentSetDeletionItem(
                            DeploymentId,
                            first.Deployment.Revision,
                            new ComputeProfileReference(
                                ResourceDeploymentTestData.ComputeProfileId,
                                1))
                    ]),
                session,
                cancellationToken));

        var summaries = result.AssertSuccess();
        Assert.Equal([secondDeploymentId, DeploymentId], summaries.Select(item => item.DeploymentId));
        Assert.All(summaries, summary => Assert.Equal(2, summary.ResourceRevision));
        Assert.Equal(
            2L,
            await fixture.DbContext.CountOutboxMessagesAsync(
                "pole.resource-deployments/v1/resource-deployment-deletion-requested"));

        var retry = await RunTypedTransactionAsync(
            (session, cancellationToken) => mutations.SetDeletion(
                new ResourceDeploymentSetDeletionRequest(
                    TenantId,
                    TestResourceType,
                    ResourceId,
                    ResourceRevision: 2,
                    summaries.Select(summary => new ResourceDeploymentSetDeletionItem(
                            summary.DeploymentId,
                            summary.DeploymentRevision,
                            summary.Compute.Profile))
                        .ToArray()),
                session,
                cancellationToken));

        _ = retry.AssertSuccess();
        Assert.Equal(
            2L,
            await fixture.DbContext.CountOutboxMessagesAsync(
                "pole.resource-deployments/v1/resource-deployment-deletion-requested"));
    }

    [Fact]
    public async Task SetMutation_RejectsEmptyAndDuplicateDeploymentSets()
    {
        var mutations = CreateMutations(new RecordingComputeProfileFence());
        var item = new ResourceDeploymentSetApplyItem(
            DeploymentId,
            ExpectedDeploymentRevision: 1,
            new ComputeProfileReference(
                ResourceDeploymentTestData.ComputeProfileId,
                ComputeProfileRevision: 1));

        await AssertStructuralRequestRejectedAsync(session =>
            mutations.SetApply(
                new ResourceDeploymentSetApplyRequest(
                    TenantId,
                    TestResourceType,
                    ResourceId,
                    ResourceRevision: 2,
                    []),
                session));
        await AssertStructuralRequestRejectedAsync(session =>
            mutations.SetApply(
                new ResourceDeploymentSetApplyRequest(
                    TenantId,
                    TestResourceType,
                    ResourceId,
                    ResourceRevision: 2,
                    [item, item]),
                session));
    }

    [Fact]
    public async Task Mutation_RequiresCallerOwnedTransaction()
    {
        var mutations = CreateMutations();
        using var session = await fixture.DbContext.Client.StartSessionAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mutations.Apply(CreateCommand(), session));

        Assert.Contains(
            "logical resource transaction",
            exception.Message,
            StringComparison.Ordinal);
    }

    private MutationOperations CreateMutations() =>
        CreateMutations(new RecordingComputeProfileFence());

    private MutationOperations CreateMutations(
        RecordingComputeProfileFence fence,
        RecordingPlacementLookup? placementLookup = null)
    {
        var defaultLookup = Services.GetRequiredService<LoadResourcePlacementInTransaction>();
        var defaultManyLookup = Services.GetRequiredService<LoadResourcePlacementsInTransaction>();
        var load = placementLookup is null ? defaultLookup : placementLookup.LoadAsync;
        var loadMany = placementLookup is null ? defaultManyLookup : placementLookup.LoadManyAsync;
        StoreOutboxWrites storeOutboxWrites = fixture.StoreOutboxWrites;
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(
            MutationAtUtc,
            TimeSpan.Zero));
        return new MutationOperations(
            new MongoRequestResourceDeploymentApply(
                fixture.DbContext.ResourceDeployments,
                storeOutboxWrites,
                load,
                fence.InvokeAsync,
                timeProvider).InvokeAsync,
            new MongoRequestResourceDeploymentDeletion(
                fixture.DbContext.ResourceDeployments,
                storeOutboxWrites,
                timeProvider).InvokeAsync,
            new MongoRetryExactResourceDeployment(
                fixture.DbContext.ResourceDeployments,
                storeOutboxWrites,
                timeProvider).InvokeAsync,
            new MongoRequestResourceDeploymentSetApply(
                fixture.DbContext.ResourceDeployments,
                storeOutboxWrites,
                loadMany,
                fence.InvokeAsync,
                timeProvider).InvokeAsync,
            new MongoRequestResourceDeploymentSetDeletion(
                fixture.DbContext.ResourceDeployments,
                storeOutboxWrites,
                timeProvider).InvokeAsync);
    }

    private sealed record MutationOperations(
        RequestResourceDeploymentApply Apply,
        RequestResourceDeploymentDeletion Deletion,
        RetryExactResourceDeployment Retry,
        RequestResourceDeploymentSetApply SetApply,
        RequestResourceDeploymentSetDeletion SetDeletion);

    private Task<Result<T, TError>> RunTypedTransactionAsync<T, TError>(
        Func<
            IClientSessionHandle,
            CancellationToken,
            Task<Result<T, TError>>> operation)
        where T : notnull
        where TError : notnull =>
        new MongoRunTransaction(fixture.Client).InvokeOutcomeAsync<Result<T, TError>>(
            async (transaction, cancellationToken) =>
            {
                var result = await operation(transaction.Session, cancellationToken);
                return result switch
                {
                    Ok<T> => transaction.Commit(result),
                    Err<TError> => transaction.Abort(result)
                };
            });

    private RecordingPlacementLookup CreateRecordingPlacementLookup() =>
        new(
            Services.GetRequiredService<LoadResourcePlacementInTransaction>(),
            Services.GetRequiredService<LoadResourcePlacementsInTransaction>());

    private IServiceProvider Services => serviceScope?.ServiceProvider ??
        throw new InvalidOperationException("The test service scope was not initialized.");

    private async Task AssertStructuralRequestRejectedAsync(
        Func<IClientSessionHandle, Task> operation)
    {
        using var session = await fixture.DbContext.Client.StartSessionAsync();
        session.StartTransaction();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => operation(session));
        }
        finally
        {
            if (session.IsInTransaction)
                await session.AbortTransactionAsync();
        }
    }

    private static ResourceDeploymentMutationRequest CreateCommand() =>
        new(
            TenantId,
            TestResourceType,
            ResourceId,
            new ComputeProfileReference(
                ResourceDeploymentTestData.ComputeProfileId,
                ComputeProfileRevision: 1));

    private async Task InsertAsync(
        RuntimeCell runtimeCell,
        ResourceDeployment deployment)
    {
        await fixture.DbContext.RuntimeCells.InsertOneAsync(runtimeCell);
        await fixture.DbContext.ResourceDeployments.InsertOneAsync(deployment);
    }

    private Task<ResourceDeployment> LoadDeploymentAsync() =>
        fixture.DbContext.ResourceDeployments
            .Find(item => item.Id == DeploymentId)
            .SingleAsync();

    private static (RuntimeCell RuntimeCell, ResourceDeployment Deployment)
        CreateAggregate(bool runtimeCellReady)
        => CreateAggregate(RuntimeCellId, TargetId, DeploymentId, runtimeCellReady);

    private static (RuntimeCell RuntimeCell, ResourceDeployment Deployment)
        CreateAggregate(
            string runtimeCellId,
            string targetId,
            string deploymentId,
            bool runtimeCellReady)
    {
        var runtimeCell = RuntimeCellFactory.Create(
            TenantId,
            ResourceGroupId,
            ScopeId,
            targetId,
            "pole-test-scope",
            new RuntimeCellFactory.CreateOptions(
                runtimeCellId,
                CreatedAtUtc));
        if (runtimeCellReady)
        {
            runtimeCell = runtimeCell.MarkReady(
                expectedGeneration: 1,
                CreatedAtUtc.AddMinutes(1));
        }

        var placement = ResolvedResourcePlacement.Create(
            runtimeCellId,
            targetId,
            runtimeCell.KubernetesNamespace,
            runtimeCellGeneration: 1,
            targetWorkloadConfigurationRevision: 1,
            ResolvedIngressConfiguration.Create(
                "traefik",
                "letsencrypt-prod"),
            storage: null,
            ResourceDeploymentTestData.Compute(),
            CreatedAtUtc);
        var deployment = ResourceDeploymentFactory.Create(
            TenantId,
            TestResourceType,
            ResourceId,
            ResourceGroupId,
            ScopeId,
            placement,
            ResourcePlacementCapability.MultiTarget,
            runtimeCellReady,
            new ResourceDeploymentFactory.CreateOptions(
                deploymentId,
                CreatedAtUtc));
        return (runtimeCell, deployment);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record PlacementMutationRejected(string Message);

    private sealed class RecordingComputeProfileFence
    {
        public int CallCount { get; private set; }
        public int FenceManyCallCount { get; private set; }
        public int LastBatchCount { get; private set; }

        public Task<Result<Unit, ComputeProfileResolutionFenceRejected>> InvokeAsync(
            IReadOnlyList<ComputeProfileResolutionFenceRequest> requests,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            if (requests.Count > 1)
                FenceManyCallCount++;
            else
                CallCount++;
            LastBatchCount = requests.Count;
            return Task.FromResult<Result<Unit, ComputeProfileResolutionFenceRejected>>(
                new Ok<Unit>(Unit.Value));
        }
    }

    private sealed class RecordingPlacementLookup(
        LoadResourcePlacementInTransaction load,
        LoadResourcePlacementsInTransaction loadMany)
    {
        public int FindCallCount { get; private set; }
        public int FindManyCallCount { get; private set; }

        public Task<ResourcePlacement?> LoadAsync(
            string tenantId,
            string runtimeCellId,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            FindCallCount++;
            return load(tenantId, runtimeCellId, session, cancellationToken);
        }

        public Task<IReadOnlyList<ResourcePlacement>> LoadManyAsync(
            string tenantId,
            IReadOnlyCollection<string> runtimeCellIds,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            FindManyCallCount++;
            return loadMany(tenantId, runtimeCellIds, session, cancellationToken);
        }
    }
}
