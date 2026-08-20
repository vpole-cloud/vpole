using System.Diagnostics.Metrics;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Jobs;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.ResourceGroups.Application.Services;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

[Collection(IntegrationTestCollection.Name)]
public sealed class ResourceDeploymentRecoveryJobTests(
    MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
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

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

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
        var persistence = new ResourceDeploymentRecoveryPersistence(
            fixture.DbContext.ResourceDeployments,
            (operation, cancellationToken) => fixture.DbContext
                .RunInTransactionAsync<long>(
                    operation,
                    cancellationToken: cancellationToken),
            (session, writes, cancellationToken) => fixture.DbContext.StoreEventAsync(
                writes,
                session,
                cancellationToken),
            new ResourcePlacementLookup(fixture.DbContext));
        var job = new ResourceDeploymentRecoveryJob(
            persistence.LoadCandidatesAsync,
            persistence.RecoverAsync,
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

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(utcNow, TimeSpan.Zero);
    }
}
