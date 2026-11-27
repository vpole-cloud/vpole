// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using k8s;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Application.Discovery.Observation;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain.Observation;
using Pole.Observation;
using Pole.Testing.Kubernetes;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.StorageProfiles;

[Collection(IntegrationTestCollection.Global2)]
public sealed class StorageObservationOwnerTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    public override Task InitializeAsync() => base.InitializeAsync();
    public override Task DisposeAsync() => base.DisposeAsync();

    [Fact]
    public async Task BurstWhileNativeReadIsBlockedProducesOneTrailingSnapshotWithoutOverlappingReads()
    {
        var context = await SeedAsync();
        var store = Store();
        var ownership = Assert.IsType<StorageObservationOwnership>(
            (await store.ClaimLeaseAsync(context, "replica-a")).Value);
        var clock = new NativeWatchTestClock();
        var source = new TestSource();
        using var handler = new ValidationHandler { BlockReadAt = 1 };
        using var client = NativeWatchTestClient.Create(handler);
        using var budget = new ObservationReadBudget(clock);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = new StorageObservationOwner(store, source, Native(client, clock, budget), clock).RunAsync(
            context,
            ownership,
            cancellation.Token);
        try
        {
            source.Send(
                new StorageObservationChange(StorageObservationChangeKind.Ready, "source-a", Inventory("class-a")));
            await UntilAsync(() => Task.FromResult(handler.Entered.Task.IsCompleted), clock);
            for (var i = 0; i < 200; i++)
                source.Send(
                    new StorageObservationChange(
                        StorageObservationChangeKind.Changed,
                        "source-a",
                        Inventory("class-a")));
            await UntilAsync(() => Task.FromResult(source.Pending == 0), clock);
            Assert.Equal(1, handler.Reads);
            Assert.Null(await store.LoadFreshSnapshotAsync(context));
            handler.Release.TrySetResult();
            await UntilAsync(
                async () => (await store.LoadControlAsync(context.DeploymentTargetId))?.ObservationVersion == 3,
                clock);
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(50, cancellation.Token);
            Assert.Equal(6, handler.Reads); // Two complete samples, each reading classes, PVs and PVCs.
            Assert.Equal(3, (await store.LoadFreshSnapshotAsync(context))!.ObservationVersion);
        }
        finally
        {
            handler.Release.TrySetResult();
            await cancellation.CancelAsync();
            try
            {
                await run;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
    }

    [Fact]
    public async Task QuietThirtySecondValidationUsesNativeReadsAndAdvancesVersionWithoutSourceChurn()
    {
        var context = await SeedAsync();
        var store = Store();
        var ownership
            = Assert.IsType<StorageObservationOwnership>((await store.ClaimLeaseAsync(context, "replica-a")).Value);
        var clock = new NativeWatchTestClock();
        var source = new TestSource();
        using var handler = new ValidationHandler();
        using var client = NativeWatchTestClient.Create(handler);
        using var budget = new ObservationReadBudget(clock);
        var native = Native(client, clock, budget);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = new StorageObservationOwner(store, source, native, clock).RunAsync(
            context,
            ownership,
            cancellation.Token);
        try
        {
            source.Send(
                new StorageObservationChange(StorageObservationChangeKind.Ready, "source-a", Inventory("class-a")));
            await UntilAsync(
                async () => (await store.LoadControlAsync(context.DeploymentTargetId))?.ObservationVersion == 2,
                clock);
            var before = (await store.LoadControlAsync(context.DeploymentTargetId))!;
            clock.Advance(TimeSpan.FromSeconds(30));
            await UntilAsync(
                async () => (await store.LoadControlAsync(context.DeploymentTargetId))!.ObservationVersion >
                    before.ObservationVersion,
                clock);
            var after = (await store.LoadControlAsync(context.DeploymentTargetId))!;
            Assert.Equal(before.SourceControlVersion, after.SourceControlVersion);
            Assert.Equal(before.Epoch, after.Epoch);
            Assert.True(after.SourceHealthy);
            Assert.Equal(6, handler.Reads);
            Assert.Equal(after.ObservationVersion, (await store.LoadFreshSnapshotAsync(context))!.ObservationVersion);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await run;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
    }

    [Fact]
    public async Task SourceLossFencesBeforeBlockedNativeValidationAndLateEvidenceCannotOverwriteRecovery()
    {
        var context = await SeedAsync();
        var store = Store();
        var ownership
            = Assert.IsType<StorageObservationOwnership>((await store.ClaimLeaseAsync(context, "replica-a")).Value);
        var clock = new NativeWatchTestClock();
        var source = new TestSource();
        using var handler = new ValidationHandler { BlockReadAt = 4 };
        using var client = NativeWatchTestClient.Create(handler);
        using var budget = new ObservationReadBudget(clock);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = new StorageObservationOwner(store, source, Native(client, clock, budget), clock).RunAsync(
            context,
            ownership,
            cancellation.Token);
        try
        {
            source.Send(
                new StorageObservationChange(StorageObservationChangeKind.Ready, "source-a", Inventory("class-a")));
            await UntilAsync(
                async () => (await store.LoadControlAsync(context.DeploymentTargetId))?.ObservationVersion == 2,
                clock);
            clock.Advance(TimeSpan.FromSeconds(30));
            await UntilAsync(() => Task.FromResult(handler.Entered.Task.IsCompleted), clock);
            source.Send(new StorageObservationChange(StorageObservationChangeKind.Unavailable));
            await UntilAsync(
                async () => await store.LoadControlAsync(context.DeploymentTargetId) is
                    { SourceHealthy: false, ObservationVersion: 3 },
                clock);
            Assert.Null(await store.LoadFreshSnapshotAsync(context));
            handler.ClassName = "class-b";
            source.Send(
                new StorageObservationChange(StorageObservationChangeKind.Ready, "source-b", Inventory("class-b")));
            await UntilAsync(
                async () => (await store.LoadControlAsync(context.DeploymentTargetId))?.ObservationVersion == 4,
                clock);
            handler.Release.TrySetResult();
            await UntilAsync(
                async () => (await store.LoadControlAsync(context.DeploymentTargetId))?.ObservationVersion == 5,
                clock);
            var snapshot = await store.LoadFreshSnapshotAsync(context);
            Assert.Equal("class-b", Assert.Single(snapshot!.StorageClasses).Name);
            Assert.Equal(5, snapshot.ObservationVersion);
        }
        finally
        {
            handler.Release.TrySetResult();
            cancellation.Cancel();
            try
            {
                await run;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
    }

    [Fact]
    public async Task DelayedOldChangedCutCannotReplaceNewerLiveInventoryOrRefreshStaleEvidence()
    {
        var context = await SeedAsync();
        var store = Store();
        var ownership
            = Assert.IsType<StorageObservationOwnership>((await store.ClaimLeaseAsync(context, "replica-a")).Value);
        var clock = new NativeWatchTestClock();
        var source = new TestSource();
        using var handler = new ValidationHandler();
        using var client = NativeWatchTestClient.Create(handler);
        using var budget = new ObservationReadBudget(clock);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = new StorageObservationOwner(store, source, Native(client, clock, budget), clock).RunAsync(
            context,
            ownership,
            cancellation.Token);
        try
        {
            source.Send(
                new StorageObservationChange(StorageObservationChangeKind.Ready, "source-a", Inventory("class-a")));
            await UntilAsync(
                async () => (await store.LoadControlAsync(context.DeploymentTargetId))?.ObservationVersion == 2,
                clock);
            handler.Expansion = true;
            clock.Advance(TimeSpan.FromSeconds(30));
            await UntilAsync(
                async () => (await store.LoadControlAsync(context.DeploymentTargetId))?.ObservationVersion == 3,
                clock);
            var newer = await store.LoadFreshSnapshotAsync(context);
            Assert.True(Assert.Single(newer!.StorageClasses).AllowVolumeExpansion);
            source.Send(
                new StorageObservationChange(
                    StorageObservationChangeKind.Changed,
                    "source-a",
                    Inventory("class-a"))); // Delayed cut still has the old null expansion field.
            await UntilAsync(
                async () => (await store.LoadControlAsync(context.DeploymentTargetId))?.ObservationVersion == 4,
                clock);
            var refreshed = await store.LoadFreshSnapshotAsync(context);
            Assert.True(Assert.Single(refreshed!.StorageClasses).AllowVolumeExpansion);
            Assert.True(refreshed.CheckedAtUtc >= newer.CheckedAtUtc);
            Assert.Equal(9, handler.Reads);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await run;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
    }

    [Fact]
    public async Task AuthoritativeNamesIgnoreCommittedInventoryAndUseOnlyCurrentLiveClassList()
    {
        var context = await SeedAsync();
        var store = Store();
        var owner = Assert.IsType<StorageObservationOwnership>(
            (await store.ClaimLeaseAsync(context, "replica-a")).Value);
        var source = Assert.IsType<StorageObservationSourceState>(
            (await store.SetSourceStateAsync(owner.Lease, context, owner.SourceControlVersion, true, "source-a"))
            .Value);
        await store.TryCommitSnapshotAsync(
            owner.Lease,
            context,
            source.SourceControlVersion,
            "source-a",
            Inventory("old-class"));
        using var handler = new ValidationHandler();
        using var client = NativeWatchTestClient.Create(handler);
        using var budget = new ObservationReadBudget(TimeProvider.System);
        var names = await Native(client, TimeProvider.System, budget).ReadNamesAsync(context, default);
        Assert.Equal(["class-a"], names!.ToArray());
        Assert.Equal(1, handler.Reads);
        Assert.Equal("old-class", Assert.Single((await store.LoadFreshSnapshotAsync(context))!.StorageClasses).Name);
    }

    private MongoStorageObservationStore Store() => new(
        Fixture.Database.GetCollection<StorageObservationControlDocument>(
            MongoStorageObservationStore.ControlsCollection),
        Fixture.Database.GetCollection<StorageObservationDemandDocument>(
            MongoStorageObservationStore.DemandsCollection),
        Fixture.Database.GetCollection<StorageObservationSnapshotDocument>(
            MongoStorageObservationStore.SnapshotsCollection),
        new MongoRunTransaction(Fixture.Client),
        (
                request, session,
                ct) =>
            new MongoFenceDeploymentTargetObservation(Fixture.DbContext.DeploymentTargets)
                .InvokeAsync(request, session, ct),
        Fixture.StoreOutboxWrites,
        new StorageObservationOptions());

    private async Task<StorageObservationContext> SeedAsync()
    {
        var id = TestData.DefaultDeploymentTargetId;
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(TestData.SeedDeploymentTarget(id));
        var context = new StorageObservationContext(id, 1);
        await Store()
            .PutDemandAsync(
                new StorageObservationDemand(
                    new StorageObservationDemandIdentity(id, "subscription", "connection", "session"),
                    DateTimeOffset.UtcNow.AddMinutes(20)),
                context);
        return context;
    }

    private static StorageObservationNativeReader Native(
        IKubernetes client,
        TimeProvider clock,
        ObservationReadBudget budget) => new(
        new FixedKubernetesClientProvider(client),
        new StorageObservationContextLoader((id, _) => Task.FromResult<LookupDeploymentTargetOutcome>(
            new DeploymentTargetLookupDto(
                id,
                "Target",
                "cluster",
                "InCluster",
                1,
                new DeploymentTargetWorkloadConfigurationLookupDto(
                    1,
                    null,
                    new DeploymentTargetNetworkSourcesDto(
                        ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty)),
                1,
                true,
                false,
                false,
                "Healthy",
                TechnicalName: "test-target",
                NetworkPolicy: DeploymentTargetNetworkPolicyDto.Unknown))),
        budget,
        new StorageObservationOptions());

    private static StorageInventoryObservation Inventory(string name) => new(
        [new StorageClassObservation(name, "driver.example", false, null, null, null)],
        [],
        [],
        true);

    private static async Task UntilAsync(Func<Task<bool>> ready, NativeWatchTestClock clock)
    {
        var timeout = Stopwatch.StartNew();
        while (!await ready())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(6))
                throw new TimeoutException("Storage observation did not reach the expected safe state.");
            clock.Advance(TimeSpan.FromMilliseconds(300));
            await Task.Delay(10);
        }
    }

    private sealed class TestSource : IStorageObservationSource
    {
        private readonly Channel<StorageObservationChange>
            changes = Channel.CreateUnbounded<StorageObservationChange>();

        private int pending;
        internal int Pending => Volatile.Read(ref pending);

        public async IAsyncEnumerable<StorageObservationChange> WatchAsync(
            StorageObservationContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var change in changes.Reader.ReadAllAsync(cancellationToken))
            {
                yield return change;
                Interlocked.Decrement(ref pending);
            }
        }

        internal void Send(StorageObservationChange change)
        {
            Interlocked.Increment(ref pending);
            changes.Writer.TryWrite(change);
        }
    }

    private sealed class ValidationHandler : DelegatingHandler
    {
        public int Reads;
        public int BlockReadAt { get; init; }

        public string ClassName { get; set; } = "class-a";

        public bool? Expansion { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref Reads);
            var className = ClassName;
            var expansion = Expansion;
            if (BlockReadAt == count)
            {
                Entered.TrySetResult();
                await Release.Task;
            } // Deliberately ignores cancellation to prove the final read fence.

            var path = request.RequestUri!.AbsolutePath;
            var kind = path.EndsWith("storageclasses", StringComparison.Ordinal) ? "StorageClassList" :
                path.EndsWith("persistentvolumes", StringComparison.Ordinal) ? "PersistentVolumeList" :
                "PersistentVolumeClaimList";
            var api = kind == "StorageClassList" ? "storage.k8s.io/v1" : "v1";
            var items = kind == "StorageClassList"
                ? JsonSerializer.Serialize(
                    new
                    {
                        metadata = new
                        {
                            name = className,
                            uid = "live-class",
                        },
                        provisioner = "driver.example",
                        allowVolumeExpansion = expansion,
                    })
                : "";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    $$"""{"apiVersion":"{{api}}","kind":"{{kind}}","metadata":{"resourceVersion":"live-version"},"items":[{{items}}]}"""),
            };
        }
    }
}
