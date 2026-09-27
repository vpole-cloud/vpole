using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Jobs;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Application.Persistence.Reads;
using Pole.Features.ResourceDeployments.Application.Persistence.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

[Collection(IntegrationTestCollection.Name)]
public sealed class ResourceDeploymentRecoveryJobTests(
    MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    private ServiceProvider? serviceProvider;
    private IServiceScope? serviceScope;
    private const string TenantId = "507f1f77bcf86cd799439071";
    private const string ResourceId = "507f1f77bcf86cd799439072";
    private const string ResourceGroupId = "507f1f77bcf86cd799439073";
    private const string ScopeId = "507f1f77bcf86cd799439074";
    private const string RuntimeCellId = "507f1f77bcf86cd799439075";
    private const string TargetId = "507f1f77bcf86cd799439076";
    private const string DeploymentId = "507f1f77bcf86cd799439077";
    private const string ClaimId = "507f1f77bcf86cd799439078";
    private static readonly ResourceTypeId TestResourceType =
        ResourceTypeId.Create("pole/test-resource");

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
    public async Task ExpiredClaim_IsRecovered_QueuesCurrentRequest_AndRecordsMetric()
    {
        var now = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
        var createdAtUtc = now.AddMinutes(-2);
        var runtimeCell = RuntimeCellFactory.Create(
                TenantId,
                ResourceGroupId,
                ScopeId,
                TargetId,
                "pole-recovery-test",
                new RuntimeCellFactory.CreateOptions(
                    RuntimeCellId,
                    createdAtUtc))
            .MarkReady(expectedGeneration: 1, createdAtUtc.AddSeconds(1));
        var deployment = ResourceDeploymentFactory.Create(
                TenantId,
                TestResourceType,
                ResourceId,
                ResourceGroupId,
                ScopeId,
                ResolvedResourcePlacement.Create(
                    RuntimeCellId,
                    TargetId,
                    runtimeCell.KubernetesNamespace,
                    runtimeCellGeneration: 1,
                    targetWorkloadConfigurationRevision: 1,
                    ingress: null,
                    storage: null,
                    ResourceDeploymentTestData.Compute(),
                    createdAtUtc),
                ResourcePlacementCapability.SingleTarget,
                runtimeCellReady: true,
                new ResourceDeploymentFactory.CreateOptions(
                    DeploymentId,
                    createdAtUtc))
            .Start(
                expectedGeneration: 1,
                ResourceDeploymentOperation.Apply,
                ClaimId,
                startedAtUtc: now.AddMinutes(-1),
                leaseExpiresAtUtc: now.AddSeconds(-30));
        await fixture.DbContext.RuntimeCells.InsertOneAsync(runtimeCell);
        await fixture.DbContext.ResourceDeployments.InsertOneAsync(deployment);

        var measurements = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ResourceDeploymentMetrics.MeterName &&
                instrument.Name == "pole.resource_deployments.recovery.stale_claims")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, measurement, _, _) => measurements.Add(measurement));
        listener.Start();

        using var metrics = new ResourceDeploymentMetrics();
        var loadCandidates = new LoadResourceDeploymentRecoveryCandidatesOperation(
            fixture.DbContext.ResourceDeployments);
        var recover = new RecoverResourceDeploymentOperation(
            fixture.DbContext.ResourceDeployments,
            (operation, cancellationToken) => fixture.DbContext
                .RunOutcomeInTransactionAsync(
                    operation,
                    cancellationToken: cancellationToken),
            (session, writes, cancellationToken) => fixture.StoreOutboxWrites(
                session,
                writes,
                cancellationToken),
            Services.GetRequiredService<LoadResourcePlacementInTransaction>());
        var job = new ResourceDeploymentRecoveryJob(
            loadCandidates.InvokeAsync,
            recover.InvokeAsync,
            metrics,
            new ResourceDeploymentSettings { RecoveryBatchSize = 10 },
            new FixedTimeProvider(now));

        await job.RecoverAsync();

        var saved = await fixture.DbContext.ResourceDeployments
            .Find(item => item.Id == DeploymentId)
            .SingleAsync();
        var pending = Assert.IsType<PendingResourceDeploymentState>(saved.State);
        Assert.Equal(1, pending.AttemptCount);
        Assert.Equal(
            1L,
            await fixture.DbContext.OutboxMessages.CountDocumentsAsync(
                message =>
                    message.PendingKey ==
                    ResourceDeploymentPendingKey.Apply(DeploymentId, 1)));
        Assert.Equal([1L], measurements);
    }

    [Fact]
    public async Task AppliedVerification_PreservesReleaseIdentity_AndAdvancesThroughBoundedBatches()
    {
        var now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        var oldest = CreateApplied(ObjectId.GenerateNewId().ToString(), now.AddMinutes(-20));
        var next = CreateApplied(ObjectId.GenerateNewId().ToString(), now.AddMinutes(-10));
        var recent = CreateApplied(ObjectId.GenerateNewId().ToString(), now.AddMinutes(-1));
        await fixture.DbContext.ResourceDeployments.InsertManyAsync([oldest, next, recent]);
        var candidates = new LoadResourceDeploymentRecoveryCandidatesOperation(fixture.DbContext.ResourceDeployments);
        var recover = new RecoverResourceDeploymentOperation(
            fixture.DbContext.ResourceDeployments,
            (operation, cancellationToken) => fixture.DbContext.RunOutcomeInTransactionAsync(
                operation, cancellationToken: cancellationToken),
            fixture.StoreOutboxWrites,
            Services.GetRequiredService<LoadResourcePlacementInTransaction>());

        Assert.Equal([oldest.Id], await candidates.InvokeAsync(now, now.AddSeconds(-30), 1));
        await recover.InvokeAsync(oldest.Id, now);
        await recover.InvokeAsync(oldest.Id, now);

        var queued = await fixture.DbContext.ResourceDeployments.Find(item => item.Id == oldest.Id).SingleAsync();
        Assert.IsType<PendingResourceDeploymentState>(queued.State);
        Assert.Equal(oldest.DesiredGeneration, queued.DesiredGeneration);
        Assert.Equal(oldest.ResourceRevision, queued.ResourceRevision);
        Assert.Equal(oldest.ResolvedPlacement.KubernetesNamespace, queued.ResolvedPlacement.KubernetesNamespace);
        Assert.Equal(oldest.ResolvedPlacement.TargetWorkloadConfigurationRevision,
            queued.ResolvedPlacement.TargetWorkloadConfigurationRevision);
        Assert.Equal(oldest.Revision + 1, queued.Revision);
        Assert.Equal(1, await fixture.DbContext.OutboxMessages.CountDocumentsAsync(
            message => message.PendingKey == ResourceDeploymentPendingKey.Apply(oldest.Id, oldest.DesiredGeneration)));
        Assert.Equal([next.Id], await candidates.InvokeAsync(now, now.AddSeconds(-30), 1));
        await recover.InvokeAsync(next.Id, now);
        Assert.Empty(await candidates.InvokeAsync(now, now.AddSeconds(-30), 1));
    }

    [Fact]
    public async Task Verification_DoesNotResurrectADeploymentWhoseDeletionWasRequested()
    {
        var now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        var deleting = CreateApplied(DeploymentId, now.AddMinutes(-20)).RequestDeletion(now.AddSeconds(-1));
        await fixture.DbContext.ResourceDeployments.InsertOneAsync(deleting);
        var recover = new RecoverResourceDeploymentOperation(
            fixture.DbContext.ResourceDeployments,
            (operation, cancellationToken) => fixture.DbContext.RunOutcomeInTransactionAsync(
                operation, cancellationToken: cancellationToken),
            fixture.StoreOutboxWrites,
            Services.GetRequiredService<LoadResourcePlacementInTransaction>());

        await recover.InvokeAsync(deleting.Id, now);

        var saved = await fixture.DbContext.ResourceDeployments.Find(item => item.Id == deleting.Id).SingleAsync();
        Assert.Equal(ResourceDeploymentOperation.Delete, saved.DesiredOperation);
        Assert.Equal(deleting.DesiredGeneration, saved.DesiredGeneration);
        Assert.Throws<InvalidOperationException>(() => saved.RequestVerification(now));
        Assert.Equal(0, await fixture.DbContext.OutboxMessages.CountDocumentsAsync(
            message => message.PendingKey == ResourceDeploymentPendingKey.Apply(saved.Id, saved.DesiredGeneration)));
    }

    private static ResourceDeployment CreateApplied(string id, DateTime completedAtUtc)
    {
        var createdAtUtc = completedAtUtc.AddMinutes(-1);
        return ResourceDeploymentFactory.Create(
                TenantId, TestResourceType, id, ResourceGroupId, ScopeId,
                ResolvedResourcePlacement.Create(
                    RuntimeCellId, TargetId, "pole-verification-test", 1, 1,
                    null, null, ResourceDeploymentTestData.Compute(), createdAtUtc),
                ResourcePlacementCapability.SingleTarget, true,
                new ResourceDeploymentFactory.CreateOptions(id, createdAtUtc))
            .Start(1, ResourceDeploymentOperation.Apply, ClaimId, createdAtUtc, completedAtUtc.AddMinutes(1))
            .Complete(1, ResourceDeploymentOperation.Apply, ClaimId, [], completedAtUtc);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(utcNow, TimeSpan.Zero);
    }

    private IServiceProvider Services => serviceScope?.ServiceProvider ??
        throw new InvalidOperationException("The test service scope was not initialized.");
}
