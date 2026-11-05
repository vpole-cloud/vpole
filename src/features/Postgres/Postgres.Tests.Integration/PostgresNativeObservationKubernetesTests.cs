using Pole.Observation;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using k8s;
using k8s.Models;
using Microsoft.FSharp.Core;
using MongoDB.Bson;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Postgres.Application.Clusters;
using Pole.Features.Postgres.Domain;
using Pole.Features.Postgres.Tests.Integration.Infrastructure;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Testing;
using Pole.Workloads;
using Xunit;
using Xunit.Abstractions;

namespace Pole.Features.Postgres.Tests.Integration;

[Trait("Layer", "Kubernetes")]
[Trait("Feature", "Postgres")]
public sealed class PostgresNativeObservationKubernetesTests(KubernetesClusterFixture fixture, ITestOutputHelper output)
    : KubernetesIntegrationTestBase(fixture)
{
    [KubernetesFact]
    public Task Native_StatefulSet_source_observes_owned_Pod_UID_change_and_controller_fence() =>
        VerifyAsync(PostgresDeploymentMode.StatefulSet);

    [KubernetesFact("cnpg")]
    public Task CNPG_source_verifies_inherited_labels_direct_instance_UIDs_and_controller_fence() =>
        VerifyAsync(PostgresDeploymentMode.Operator);

    private async Task VerifyAsync(PostgresDeploymentMode mode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        using var client = new Kubernetes(Target.CreateConfiguration(), new KubernetesWatchHeaders());
        var cluster = PostgresCluster.Create(PostgresTestData.TenantId,
            "postgres-observation",
            Id(),
            17,
            mode,
            PostgresTestData.DefaultStorageProfileId,
            new CreatePostgresClusterOptions(FSharpOption<string>.Some(Id()), 1, 1));
        var targetId = PostgresTestData.DefaultDeploymentTargetId;
        var metadata = new PostgresResourceDeploymentMetadata(Id(), Id(), Id(), targetId, 1);
        var identity = new WorkloadIdentityLabels(cluster.TenantId,
            cluster.GroupId,
            metadata.IsolationScopeId,
            metadata.RuntimeCellId,
            targetId,
            "Postgres",
            cluster.Id,
            metadata.DeploymentId);
        var compute = new ResourceDeploymentComputeConfiguration(PostgresTestData.DefaultComputeProfileId,
            1,
            targetId,
            "test",
            new(250, 256),
            new(1000, 1024),
            ComputeArchitecture.Any,
            [],
            [],
            [],
            [],
            [],
            null);
        var context = new ResourceDeploymentObservationContext(new ResourceDeploymentReconciliationSnapshot(
                metadata.DeploymentId,
                cluster.TenantId,
                PostgresResourceDeployment.ResourceType,
                cluster.Id,
                cluster.GroupId,
                metadata.IsolationScopeId,
                metadata.RuntimeCellId,
                1,
                1,
                1,
                DateTime.UtcNow,
                new ResourceDeploymentResolvedPlacementSnapshot(targetId,
                    1,
                    NamespaceName,
                    null,
                    new ResourceDeploymentStorageConfiguration(PostgresTestData.DefaultStorageProfileId,
                        1,
                        Target.StorageClassName),
                    compute)),
            1,
            1);
        var settings = new PostgresSettings
        {
            OperatorImage = new PostgresImageSettings { Server = "ghcr.io", Name = "cloudnative-pg/postgresql" },
            StatefulSetImage = new PostgresImageSettings { Server = "docker.io", Name = "postgres" }
        };
        IPostgresClusterDeploymentManager manager = new PostgresClusterDeploymentManager(settings);
        using var projection = new ManagedProjectionFixture(client, cluster, NamespaceName);
        await projection.ApplyAsync(Guid.NewGuid().ToString("N")); // Ephemeral; never persisted or printed by the test.
        await manager.ApplyAsync(client,
            cluster,
            projection.Credentials,
            NamespaceName,
            Target.StorageClassName,
            new KubernetesComputeConfiguration(new(250, 256),
                new(1000, 1024),
                ComputeArchitecture.Any,
                [],
                [],
                [],
                [],
                [],
                null),
            metadata,
            deadline.Token);
        var name = PostgresClusterKubernetesBuilder.buildResourceName(cluster.Id);
        var controller = await ControllerAsync();
        var binding = controller.Uid;
        AssertOwned(controller, identity);
        var provider = new ExactClient(client, targetId);
        var load = new LoadPostgresClusterById((tenant, id, _) =>
        {
            Assert.Equal(cluster.TenantId, tenant);
            Assert.Equal(cluster.Id, id);
            return Task.FromResult(FSharpOption<PostgresCluster>.Some(cluster));
        });
        IResourceDeploymentBoundObserver observer = new PostgresResourceDeploymentObserver(load, provider);
        ResourceDeploymentBoundObservation? ready = null;
        var until = Stopwatch.StartNew();
        while (until.Elapsed < TimeSpan.FromMinutes(4))
        {
            ready = await observer.ObserveBoundAsync(context, binding, deadline.Token);
            if (ready.Observation is ObservedWorkload { Ready: true })
                break;
            await Task.Delay(1000, deadline.Token);
        }

        if (ready?.Observation is not ObservedWorkload { Ready: true })
        {
            output.WriteLine("Readiness result kind: {0}", ready?.Observation.GetType().Name);
            if (ready?.Observation is ObservedWorkload summary)
                output.WriteLine("Ready={0} current={1} desired={2} updated={3}",
                    summary.Ready,
                    summary.CurrentReplicas,
                    summary.DesiredReplicas,
                    summary.UpdatedReplicas);
            if (mode == PostgresDeploymentMode.Operator)
            {
                var native = await client.CustomObjects.GetNamespacedCustomObjectAsync("postgresql.cnpg.io",
                    "v1",
                    NamespaceName,
                    "clusters",
                    name,
                    cancellationToken: deadline.Token);
                var json = JsonSerializer.SerializeToElement(native);
                output.WriteLine("Controller generation: {0}",
                    json.GetProperty("metadata").GetProperty("generation").GetInt64());
                if (json.TryGetProperty("status", out var status))
                {
                    foreach (var key in new[] { "instances", "readyInstances" })
                        if (status.TryGetProperty(key, out var count) && count.ValueKind == JsonValueKind.Number)
                            output.WriteLine("{0}: {1}", key, count.GetInt64());
                    if (status.TryGetProperty("conditions", out var conditions) &&
                        conditions.ValueKind == JsonValueKind.Array)
                        foreach (var condition in conditions.EnumerateArray().Take(16))
                        {
                            var type = condition.GetProperty("type").GetString();
                            if (type is not ("Ready" or "Initialized" or "ContinuousArchiving"
                                or "LastBackupSucceeded"))
                                type = "Other";
                            var state = condition.GetProperty("status").GetString();
                            if (state is not ("True" or "False" or "Unknown"))
                                state = "Unknown";
                            var generation =
                                condition.TryGetProperty("observedGeneration", out var value) &&
                                value.ValueKind == JsonValueKind.Number
                                    ? value.GetInt64()
                                    : -1;
                            output.WriteLine("Condition: {0}/{1}/observedGeneration={2}", type, state, generation);
                        }
                }
            }
        }

        Assert.True(ready?.Observation is ObservedWorkload { Ready: true });
        var instance = Assert.Single(Assert.IsType<AvailableWorkloadDiagnostics>(ready!.Diagnostics).Instances);
        var pod = await client.CoreV1.ReadNamespacedPodAsync(instance.Name,
            NamespaceName,
            cancellationToken: deadline.Token);
        Assert.Equal(instance.Uid, pod.Metadata.Uid);
        AssertOwned(pod.Metadata, identity);
        Assert.True(KubernetesOwnedWatchItem.ControlledBy(pod.Metadata,
            binding,
            mode == PostgresDeploymentMode.Operator ? "Cluster" : "StatefulSet",
            mode == PostgresDeploymentMode.Operator ? "postgresql.cnpg.io/v1" : "apps/v1"));
        var service = await client.CoreV1.ReadNamespacedServiceAsync(
            PostgresClusterKubernetesBuilder.buildReadWriteServiceName(cluster.Id),
            NamespaceName,
            cancellationToken: deadline.Token);
        AssertOwned(service.Metadata, identity);
        if (mode == PostgresDeploymentMode.Operator)
            Assert.True(KubernetesOwnedWatchItem.ControlledBy(service.Metadata,
                binding,
                "Cluster",
                "postgresql.cnpg.io/v1"));
        var slices = await client.DiscoveryV1.ListNamespacedEndpointSliceAsync(NamespaceName,
            labelSelector: $"kubernetes.io/service-name={service.Metadata.Name}",
            cancellationToken: deadline.Token);
        Assert.NotEmpty(slices.Items);
        Assert.All(slices.Items,
            slice => Assert.True(
                KubernetesOwnedWatchItem.ControlledBy(slice.Metadata, service.Metadata.Uid, "Service", "v1")));
        IResourceDeploymentObservationChangeSource source = new PostgresResourceDeploymentChangeSource(load,
            provider,
            new LookupDeploymentTarget((id, _) => Task.FromResult<LookupDeploymentTargetOutcome>(TargetDto(id))),
            new Budget(),
            TimeProvider.System);
        using var watchLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var changes = source.WatchAsync(context, watchLifetime.Token).GetAsyncEnumerator();
        try
        {
            await NextAsync(changes,
                ResourceDeploymentObservationChangeKind.SourceReady,
                TimeSpan.FromSeconds(30),
                watchLifetime);
            Assert.Equal(binding, changes.Current.BindingId);
            var freshController = await ControllerAsync();
            Assert.Equal(binding, freshController.Uid);
            AssertOwned(freshController, identity);
            var freshPod =
                await client.CoreV1.ReadNamespacedPodAsync(instance.Name,
                    NamespaceName,
                    cancellationToken: deadline.Token);
            Assert.Equal(instance.Uid, freshPod.Metadata.Uid);
            AssertOwned(freshPod.Metadata, identity);
            Assert.True(KubernetesOwnedWatchItem.ControlledBy(freshPod.Metadata,
                binding,
                mode == PostgresDeploymentMode.Operator ? "Cluster" : "StatefulSet",
                mode == PostgresDeploymentMode.Operator ? "postgresql.cnpg.io/v1" : "apps/v1"));
            var latency = Stopwatch.StartNew();
            await client.CoreV1.DeleteNamespacedPodAsync(instance.Name,
                NamespaceName,
                body: new V1DeleteOptions
                {
                    GracePeriodSeconds = 0,
                    Preconditions = new V1Preconditions { Uid = instance.Uid }
                },
                cancellationToken: deadline.Token);
            await NextAsync(changes,
                ResourceDeploymentObservationChangeKind.Invalidated,
                TimeSpan.FromSeconds(5),
                watchLifetime);
            output.WriteLine("Native source hint latency: {0} ms", latency.ElapsedMilliseconds);
            Assert.True(latency.Elapsed <=
                        TimeSpan.FromSeconds(5)); // Native source boundary only; one sample is not p95/UI evidence.
            Assert.Equal(binding, changes.Current.BindingId);
            var postDelete = await observer.ObserveBoundAsync(context, binding, deadline.Token);
            if (postDelete.Diagnostics is AvailableWorkloadDiagnostics after)
                Assert.DoesNotContain(after.Instances, item => item.Uid == instance.Uid);
            freshController = await ControllerAsync();
            Assert.Equal(binding, freshController.Uid);
            AssertOwned(freshController, identity);
            var patch = new V1Patch(JsonSerializer.Serialize(new
            {
                metadata = new
                {
                    uid = binding,
                    labels = new Dictionary<string, string> { ["pole.io/resource-id"] = "observation-test-foreign" }
                }
            }),
                V1Patch.PatchType.MergePatch);
            if (mode == PostgresDeploymentMode.Operator)
                await client.CustomObjects.PatchNamespacedCustomObjectAsync(patch,
                    "postgresql.cnpg.io",
                    "v1",
                    NamespaceName,
                    "clusters",
                    name,
                    cancellationToken: deadline.Token);
            else
                await client.AppsV1.PatchNamespacedStatefulSetAsync(patch,
                    name,
                    NamespaceName,
                    cancellationToken: deadline.Token);
            await NextAsync(changes,
                ResourceDeploymentObservationChangeKind.BindingChanged,
                TimeSpan.FromSeconds(5),
                watchLifetime);
            Assert.Null(changes.Current.BindingId);
            Assert.Null((await observer.ObserveBoundAsync(context, binding, deadline.Token)).BindingId);
        }
        finally
        {
            watchLifetime.Cancel();
            await changes.DisposeAsync();
        }

        Assert.Equal(binding,
            (await ControllerAsync()).Uid); // Cached target client remains caller-owned after source shutdown.
        // Base cleanup revalidates namespace UID/run/worktree; only this test's namespace, projections and PVCs are deleted.

        async Task<V1ObjectMeta> ControllerAsync()
        {
            if (mode == PostgresDeploymentMode.StatefulSet)
                return (await client.AppsV1.ReadNamespacedStatefulSetAsync(name,
                    NamespaceName,
                    cancellationToken: deadline.Token)).Metadata;
            var native = await client.CustomObjects.GetNamespacedCustomObjectAsync("postgresql.cnpg.io",
                "v1",
                NamespaceName,
                "clusters",
                name,
                cancellationToken: deadline.Token);
            var json = JsonSerializer.SerializeToElement(native);
            return KubernetesJson.Deserialize<V1ObjectMeta>(json.GetProperty("metadata").GetRawText());
        }
    }

    private void AssertOwned(V1ObjectMeta meta, WorkloadIdentityLabels identity)
    {
        Assert.Equal(NamespaceName, meta.NamespaceProperty);
        Assert.False(string.IsNullOrEmpty(meta.Uid));
        Assert.True(KubernetesWorkloadObservation.Owns(meta, identity));
    }

    private static async Task NextAsync(IAsyncEnumerator<ResourceDeploymentObservationChange> changes,
        ResourceDeploymentObservationChangeKind kind,
        TimeSpan timeout,
        CancellationTokenSource lifetime)
    {
        var pending = NextCoreAsync();
        try
        {
            await pending.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            lifetime.Cancel();
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
            }

            throw;
        }

        async Task NextCoreAsync()
        {
            for (var count = 0; count < 200; count++)
            {
                Assert.True(await changes.MoveNextAsync());
                if (changes.Current.Kind == kind)
                    return;
            }

            Assert.Fail($"No {kind} transition.");
        }
    }

    private static string Id() => ObjectId.GenerateNewId().ToString();

    private static DeploymentTargetLookupDto TargetDto(string id) => new(id,
        "test",
        "cluster",
        "kubeconfig",
        1,
        new DeploymentTargetWorkloadConfigurationLookupDto(1,
            null,
            new DeploymentTargetNetworkSourcesDto(ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty)),
        1,
        true,
        true,
        false,
        "Healthy",
        TechnicalName: "test-target",
        NetworkPolicy: DeploymentTargetNetworkPolicyDto.Unknown);

    private sealed class ExactClient(IKubernetes client, string target) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string id, CancellationToken ct = default)
        {
            Assert.Equal(target, id);
            return Task.FromResult(client);
        }

        public Task<IKubernetes> GetDefaultAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException();

        public Task<IKubernetes> GetForCleanupAsync(string id, CancellationToken ct = default) =>
            throw new InvalidOperationException();
    }

    private sealed class Budget : IObservationReadBudget
    {
        public Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct = default) => read(ct);
    }
}
