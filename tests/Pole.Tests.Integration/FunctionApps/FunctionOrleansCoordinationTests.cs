using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Orleans;
using Orleans.Configuration;
using Pole.Features.Functions.Runtime.Coordination;
using Pole.Features.Functions.Runtime.Gateway;
using Pole.Server.Extensions;
using Pole.Platform.Hosting;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

[Collection(IntegrationTestCollection.Name)]
public sealed class FunctionOrleansCoordinationTests(MongoReplicaSetFixture fixture)
{
    [Fact]
    public async Task Expired_scale_down_barrier_requires_activation_fence_until_applied()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var databaseName = $"pole_orleans_fence_{suffix}";
        var collectionPrefix = $"FunctionsFence_{suffix}_";
        var silo = CreateSilo(
            databaseName,
            collectionPrefix,
            $"functions-fence-{suffix}",
            "fence-silo",
            new FunctionGatewayOptions { MaxPendingRequestsPerRevision = 2 },
            new FunctionCoordinationOptions
            {
                RecoveryGraceSeconds = 0,
                ScaleTransitionLeaseSeconds = 1,
                ScalerOwnershipLeaseSeconds = 60
            });
        try
        {
            await silo.StartAsync();
            var key = new FunctionRevisionCoordinationKey("tenant", "app", "revision");
            var grain = silo.Services.GetRequiredService<IGrainFactory>()
                .GetGrain<IFunctionRevisionCoordinatorGrain>(key.ToGrainKey());
            var ownership = await grain.TryAcquireScalerOwnershipAsync(
                new FunctionScalerOwnershipRequest("owner-operation", "owner"));
            var ownerToken = Assert.IsType<FunctionScalerOwnershipToken>(ownership.Token);
            var quiescing = await grain.BeginQuiescingAsync(
                new FunctionQuiescingBeginRequest("quiescing-operation", ownerToken));
            Assert.Equal(FunctionQuiescingDecisionKind.Granted, quiescing.Kind);

            await Task.Delay(TimeSpan.FromMilliseconds(1_200));

            var firstRequest = new FunctionAdmissionRequest(
                "first", "first", "handler", FunctionAdmissionTriggerKind.Http, 0);
            var first = await grain.AcquireAdmissionAsync(firstRequest);
            var firstToken = Assert.IsType<FunctionAdmissionToken>(first.Token);
            Assert.True(first.RequiresActivationFence);
            var replayedFirst = await grain.AcquireAdmissionAsync(firstRequest);
            Assert.Equal(firstToken, replayedFirst.Token);
            Assert.True(replayedFirst.RequiresActivationFence);
            Assert.Equal(
                FunctionAdmissionReleaseDecision.Released,
                await grain.ReleaseAdmissionAsync(new FunctionAdmissionReleaseRequest(
                    firstToken.AdmissionId,
                    firstToken.LeaseId,
                    firstToken.CoordinatorIncarnation)));

            var second = await grain.AcquireAdmissionAsync(new FunctionAdmissionRequest(
                "second", "second", "handler", FunctionAdmissionTriggerKind.Timer, 0));
            var secondToken = Assert.IsType<FunctionAdmissionToken>(second.Token);
            Assert.True(second.RequiresActivationFence);
            var activation = await grain.BeginActivationAsync(
                new FunctionActivationBeginRequest("recovery-activation", 1));
            var activationLease = Assert.IsType<FunctionActivationLease>(activation.Lease);
            Assert.Equal(
                FunctionActivationCompleteDecision.Completed,
                await grain.CompleteActivationAsync(new FunctionActivationCompleteRequest(
                    activationLease.LeaseId,
                    activationLease.Generation,
                    FunctionCoordinationCompletionOutcome.Applied)));
            Assert.Equal(
                FunctionAdmissionReleaseDecision.Released,
                await grain.ReleaseAdmissionAsync(new FunctionAdmissionReleaseRequest(
                    secondToken.AdmissionId,
                    secondToken.LeaseId,
                    secondToken.CoordinatorIncarnation)));

            var afterFence = await grain.AcquireAdmissionAsync(new FunctionAdmissionRequest(
                "third", "third", "handler", FunctionAdmissionTriggerKind.Http, 0));
            Assert.False(afterFence.RequiresActivationFence);
        }
        finally
        {
            await silo.StopAsync();
            silo.Dispose();
            await fixture.Client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Expired_scaler_owner_cannot_complete_pending_scale()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var databaseName = $"pole_orleans_scale_owner_{suffix}";
        var collectionPrefix = $"FunctionsScaleOwner_{suffix}_";
        var silo = CreateSilo(
            databaseName,
            collectionPrefix,
            $"functions-scale-owner-{suffix}",
            "scale-owner-silo",
            coordinationOptions: new FunctionCoordinationOptions
            {
                RecoveryGraceSeconds = 0,
                ScaleTransitionLeaseSeconds = 60,
                ScalerOwnershipLeaseSeconds = 1
            });
        try
        {
            await silo.StartAsync();
            var key = new FunctionRevisionCoordinationKey("tenant", "app", "revision");
            var grain = silo.Services.GetRequiredService<IGrainFactory>()
                .GetGrain<IFunctionRevisionCoordinatorGrain>(key.ToGrainKey());
            var firstOwnership = await grain.TryAcquireScalerOwnershipAsync(
                new FunctionScalerOwnershipRequest("first-operation", "first-owner"));
            var firstToken = Assert.IsType<FunctionScalerOwnershipToken>(firstOwnership.Token);
            var scale = await grain.BeginScaleAsync(new FunctionScaleBeginRequest(
                "scale-operation",
                firstToken,
                DesiredReplicas: 1,
                QuiescingEpoch: null,
                ObservedReplicas: 0));
            var scaleLease = Assert.IsType<FunctionScaleLease>(scale.Lease);

            await Task.Delay(TimeSpan.FromMilliseconds(1_200));

            Assert.Equal(
                FunctionScaleCompleteDecision.Stale,
                await grain.CompleteScaleAsync(new FunctionScaleCompleteRequest(
                    scaleLease.IntentId,
                    scaleLease.Generation,
                    FunctionCoordinationCompletionOutcome.Applied,
                    firstToken)));
            Assert.NotNull((await grain.GetSnapshotAsync()).PendingScaleIntent);

            var recoveredOwnership = await grain.TryAcquireScalerOwnershipAsync(
                new FunctionScalerOwnershipRequest("recovery-operation", "recovery-owner"));
            var recoveredToken = Assert.IsType<FunctionScalerOwnershipToken>(recoveredOwnership.Token);
            Assert.Equal(
                FunctionScaleCompleteDecision.Completed,
                await grain.CompleteScaleAsync(new FunctionScaleCompleteRequest(
                    scaleLease.IntentId,
                    scaleLease.Generation,
                    FunctionCoordinationCompletionOutcome.Applied,
                    recoveredToken)));
        }
        finally
        {
            await silo.StopAsync();
            silo.Dispose();
            await fixture.Client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Two_silos_share_admission_activation_traffic_and_persisted_revision_state()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var databaseName = $"pole_orleans_{suffix}";
        var collectionPrefix = $"Functions_{suffix}_";
        var clusterId = $"functions-{suffix}";
        var trafficClock = new ManualTrafficTimeProvider();
        var first = CreateSilo(databaseName, collectionPrefix, clusterId, "silo-1",
            trafficTimeProvider: trafficClock);
        var second = CreateSilo(databaseName, collectionPrefix, clusterId, "silo-2",
            trafficTimeProvider: trafficClock);
        try
        {
            await first.StartAsync();
            await second.StartAsync();

            var revisionKey = new FunctionRevisionCoordinationKey(
                "tenant/a",
                "function.app",
                "revision:1");
            var firstRevision = first.Services.GetRequiredService<IGrainFactory>()
                .GetGrain<IFunctionRevisionCoordinatorGrain>(revisionKey.ToGrainKey());
            var secondRevision = second.Services.GetRequiredService<IGrainFactory>()
                .GetGrain<IFunctionRevisionCoordinatorGrain>(revisionKey.ToGrainKey());

            var admissions = await Task.WhenAll(
                firstRevision.AcquireAdmissionAsync(new FunctionAdmissionRequest(
                    "http:first",
                    "first",
                    "webhook",
                    FunctionAdmissionTriggerKind.Http,
                    0)),
                secondRevision.AcquireAdmissionAsync(new FunctionAdmissionRequest(
                    "http:second",
                    "second",
                    "webhook",
                    FunctionAdmissionTriggerKind.Http,
                    0)));
            var grantedAdmission = Assert.Single(
                admissions,
                decision => decision.Kind == FunctionAdmissionDecisionKind.Granted);
            Assert.Single(
                admissions,
                decision => decision.Rejection == FunctionAdmissionRejection.RequestLimitExceeded);

            var activationResults = await Task.WhenAll(
                firstRevision.BeginActivationAsync(new FunctionActivationBeginRequest("activate-1", 1)),
                secondRevision.BeginActivationAsync(new FunctionActivationBeginRequest("activate-2", 1)));
            Assert.All(activationResults, result =>
                Assert.Equal(FunctionActivationBeginDecisionKind.Granted, result.Kind));
            Assert.Equal(activationResults[0].Lease, activationResults[1].Lease);
            var activationLease = Assert.IsType<FunctionActivationLease>(activationResults[0].Lease);
            Assert.Equal(
                FunctionActivationCompleteDecision.Completed,
                await secondRevision.CompleteActivationAsync(new FunctionActivationCompleteRequest(
                    activationLease.LeaseId,
                    activationLease.Generation,
                    FunctionCoordinationCompletionOutcome.Applied)));

            var trafficKey = new FunctionAppCoordinationKey("tenant/a", "function.app");
            var firstTraffic = first.Services.GetRequiredService<IGrainFactory>()
                .GetGrain<IFunctionAppTrafficGrain>(trafficKey.ToGrainKey());
            var secondTraffic = second.Services.GetRequiredService<IGrainFactory>()
                .GetGrain<IFunctionAppTrafficGrain>(trafficKey.ToGrainKey());
            var traffic = await Task.WhenAll(
                firstTraffic.TryAcquireAsync(new FunctionTrafficRequest("request-1", "webhook")),
                secondTraffic.TryAcquireAsync(new FunctionTrafficRequest("request-2", "webhook")));
            Assert.Single(traffic, decision => decision.Kind == FunctionTrafficDecisionKind.Granted);
            Assert.Single(
                traffic,
                decision => decision.Rejection == FunctionTrafficRejection.RateLimitExceeded);

            trafficClock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(FunctionTrafficDecisionKind.Granted,
                (await secondTraffic.TryAcquireAsync(new FunctionTrafficRequest("request-3", "webhook"))).Kind);
            Assert.Equal(FunctionTrafficRejection.RateLimitExceeded,
                (await firstTraffic.TryAcquireAsync(new FunctionTrafficRequest("request-4", "webhook"))).Rejection);

            var admissionToken = Assert.IsType<FunctionAdmissionToken>(grantedAdmission.Token);
            Assert.InRange(
                admissionToken.ExpiresAtUtc - DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(16));
            var renewal = await firstRevision.RenewAdmissionAsync(
                new FunctionAdmissionRenewalRequest(admissionToken));
            Assert.Equal(FunctionAdmissionRenewalDecisionKind.Renewed, renewal.Kind);
            var renewedAdmissionToken = Assert.IsType<FunctionAdmissionToken>(renewal.Token);
            Assert.True(renewedAdmissionToken.ExpiresAtUtc > admissionToken.ExpiresAtUtc.AddMinutes(2));
            Assert.Equal(
                FunctionAdmissionReleaseDecision.Released,
                await secondRevision.ReleaseAdmissionAsync(new FunctionAdmissionReleaseRequest(
                    renewedAdmissionToken.AdmissionId,
                    renewedAdmissionToken.LeaseId,
                    renewedAdmissionToken.CoordinatorIncarnation)));

            var ownership = await firstRevision.TryAcquireScalerOwnershipAsync(
                new FunctionScalerOwnershipRequest("scale-owner-1", "silo-1"));
            var ownershipToken = Assert.IsType<FunctionScalerOwnershipToken>(ownership.Token);
            var quiescing = await firstRevision.BeginQuiescingAsync(
                new FunctionQuiescingBeginRequest("quiesce-1", ownershipToken));
            var quiescingLease = Assert.IsType<FunctionQuiescingLease>(quiescing.Lease);
            var blocked = await secondRevision.AcquireAdmissionAsync(new FunctionAdmissionRequest(
                "timer:blocked",
                "blocked",
                "webhook",
                FunctionAdmissionTriggerKind.Timer,
                0));
            Assert.Equal(FunctionAdmissionDecisionKind.Rejected, blocked.Kind);
            Assert.Equal(FunctionAdmissionRejection.RevisionQuiescing, blocked.Rejection);
            Assert.True(blocked.RetryAfterMilliseconds > 0);
            Assert.Equal(
                FunctionQuiescingCompleteDecision.Completed,
                await secondRevision.CompleteQuiescingAsync(new FunctionQuiescingCompleteRequest(
                    ownershipToken,
                    quiescingLease.LeaseId,
                    quiescingLease.Epoch,
                    FunctionQuiescingCompletionOutcome.Aborted)));

            var pendingScale = await secondRevision.BeginScaleAsync(new FunctionScaleBeginRequest(
                "scale-to-two",
                ownershipToken,
                DesiredReplicas: 2,
                QuiescingEpoch: null,
                ObservedReplicas: 1));
            var pendingScaleLease = Assert.IsType<FunctionScaleLease>(pendingScale.Lease);

            var beforeRestart = await firstRevision.GetSnapshotAsync();
            Assert.Equal(2, beforeRestart.DesiredReplicas);
            Assert.Equal(pendingScaleLease.Generation, beforeRestart.PendingScaleIntent?.Generation);
            Assert.True(beforeRestart.Generation > activationLease.Generation);
            var persisted = await fixture.Client.GetDatabase(databaseName)
                .GetCollection<BsonDocument>(
                    collectionPrefix + FunctionCoordinationStorage.RevisionStateName)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .SingleAsync();
            Assert.True(persisted["_doc"].AsBsonDocument["data"].IsBsonBinaryData);

            await second.StopAsync();
            await first.StopAsync();

            var restarted = CreateSilo(databaseName, collectionPrefix, clusterId, "silo-3");
            try
            {
                await restarted.StartAsync();
                var recovered = await restarted.Services.GetRequiredService<IGrainFactory>()
                    .GetGrain<IFunctionRevisionCoordinatorGrain>(revisionKey.ToGrainKey())
                    .GetSnapshotAsync();
                Assert.Equal(2, recovered.DesiredReplicas);
                Assert.Equal(beforeRestart.Generation, recovered.Generation);
                Assert.NotEqual(beforeRestart.CoordinatorIncarnation, recovered.CoordinatorIncarnation);
                Assert.Equal(pendingScaleLease.Generation, recovered.PendingScaleIntent?.Generation);

                var restartedRevision = restarted.Services.GetRequiredService<IGrainFactory>()
                    .GetGrain<IFunctionRevisionCoordinatorGrain>(revisionKey.ToGrainKey());
                var staleOwner = await restartedRevision.BeginScaleAsync(new FunctionScaleBeginRequest(
                    "scale-to-two",
                    ownershipToken,
                    DesiredReplicas: 2,
                    QuiescingEpoch: null,
                    ObservedReplicas: 1));
                Assert.Equal(FunctionScaleBeginDecisionKind.Stale, staleOwner.Kind);

                var recoveredOwnership = await restartedRevision.TryAcquireScalerOwnershipAsync(
                    new FunctionScalerOwnershipRequest("scale-owner-2", "silo-3"));
                var recoveredOwnershipToken = Assert.IsType<FunctionScalerOwnershipToken>(
                    recoveredOwnership.Token);
                Assert.Equal(
                    FunctionScaleCompleteDecision.Stale,
                    await restartedRevision.CompleteScaleAsync(new FunctionScaleCompleteRequest(
                        pendingScaleLease.IntentId,
                        pendingScaleLease.Generation,
                        FunctionCoordinationCompletionOutcome.Applied,
                        ownershipToken)));
                Assert.NotNull((await restartedRevision.GetSnapshotAsync()).PendingScaleIntent);

                var recoveredScale = await restartedRevision.BeginScaleAsync(new FunctionScaleBeginRequest(
                    "scale-to-two",
                    recoveredOwnershipToken,
                    DesiredReplicas: 2,
                    QuiescingEpoch: null,
                    ObservedReplicas: 1));
                Assert.Equal(FunctionScaleBeginDecisionKind.Granted, recoveredScale.Kind);
                Assert.Equal(pendingScaleLease, recoveredScale.Lease);
                Assert.Equal(
                    FunctionScaleCompleteDecision.Completed,
                    await restartedRevision.CompleteScaleAsync(new FunctionScaleCompleteRequest(
                        pendingScaleLease.IntentId,
                        pendingScaleLease.Generation,
                        FunctionCoordinationCompletionOutcome.Applied,
                        recoveredOwnershipToken)));
                var completed = await restartedRevision.GetSnapshotAsync();
                Assert.Equal(2, completed.DesiredReplicas);
                Assert.Null(completed.PendingScaleIntent);
            }
            finally
            {
                await restarted.StopAsync();
                restarted.Dispose();
            }
        }
        finally
        {
            await first.StopAsync();
            await second.StopAsync();
            first.Dispose();
            second.Dispose();
            await fixture.Client.DropDatabaseAsync(databaseName);
        }
    }

    private IHost CreateSilo(
        string databaseName,
        string collectionPrefix,
        string clusterId,
        string siloName,
        FunctionGatewayOptions? gatewayOptions = null,
        FunctionCoordinationOptions? coordinationOptions = null,
        TimeProvider? trafficTimeProvider = null)
    {
        var siloPort = GetFreePort();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{FunctionOrleansOptions.SectionName}:ServiceId"] = "pole-functions-integration-tests",
            [$"{FunctionOrleansOptions.SectionName}:ClusterId"] = clusterId,
            [$"{FunctionOrleansOptions.SectionName}:DatabaseName"] = databaseName,
            [$"{FunctionOrleansOptions.SectionName}:CollectionPrefix"] = collectionPrefix,
            [$"{FunctionOrleansOptions.SectionName}:UseKubernetesHosting"] = "false",
            [$"{FunctionOrleansOptions.SectionName}:AdvertisedAddress"] = IPAddress.Loopback.ToString(),
            [$"{FunctionOrleansOptions.SectionName}:SiloPort"] = siloPort.ToString(),
            [$"{FunctionOrleansOptions.SectionName}:GatewayPort"] = "0",
            [$"{FunctionOrleansOptions.SectionName}:ResponseTimeoutSeconds"] = "10"
        });
        builder.Services.AddSingleton(fixture.Client);
        builder.Services.AddSingleton(trafficTimeProvider ?? TimeProvider.System);
        // Orleans subsystem timers stay real; only the application's monotonic budget is controlled.
        builder.Services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, TimeProvider.System);
        builder.Services.AddSingleton(gatewayOptions ?? new FunctionGatewayOptions
        {
            MaxPendingRequestsPerRevision = 1,
            PublicRequestsPerSecond = 1,
            PublicBurst = 1
        });
        builder.Services.AddSingleton(coordinationOptions ?? new FunctionCoordinationOptions
        {
            RecoveryGraceSeconds = 0,
            ScalerOwnershipLeaseSeconds = 60
        });
        builder.Services.Configure<SiloOptions>(options => options.SiloName = siloName);
        builder.AddFunctionOrleansHosting();
        return builder.Build();
    }

    private sealed class ManualTrafficTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
