using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using k8s;
using k8s.Models;
using MongoDB.Bson;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Tests.Integration.Infrastructure;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Testing;
using Pole.Workloads;
using Xunit;
using Xunit.Abstractions;

namespace Pole.Features.RedisServices.Tests.Integration.RedisServices;

[Trait("Layer", "Kubernetes")]
[Trait("Feature", "RedisServices")]
public sealed class RedisNativeObservationKubernetesTests(KubernetesClusterFixture fixture, ITestOutputHelper output)
    : KubernetesIntegrationTestBase(fixture)
{
    [KubernetesFact]
    public async Task Owned_StatefulSet_source_observes_real_Pod_UID_change_and_withdraws_foreign_controller()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var client = new Kubernetes(Target.CreateConfiguration(), new KubernetesWatchHeaders());
        var targetId = RedisTestData.DefaultDeploymentTargetId;
        var resource = RedisFactory.Create(RedisTestData.TenantId,
            "redis-observation",
            Id(),
            new RedisSpec(new Storage(1, RedisTestData.DefaultStorageProfileId)),
            RedisFactory.DefaultCreateOptions);
        var deploymentId = Id();
        var scopeId = Id();
        var cellId = Id();
        var identity = new WorkloadIdentityLabels(resource.TenantId,
            resource.ResourceGroupId,
            scopeId,
            cellId,
            targetId,
            "Redis",
            resource.Id,
            deploymentId);
        var compute = new ResourceDeploymentComputeConfiguration(Id(),
            1,
            targetId,
            "test",
            new(250, 128),
            new(1000, 256),
            ComputeArchitecture.Any,
            [],
            [],
            [],
            [],
            [],
            null);
        var storage =
            new ResourceDeploymentStorageConfiguration(RedisTestData.DefaultStorageProfileId,
                1,
                Target.StorageClassName);
        var context = new ResourceDeploymentObservationContext(new ResourceDeploymentReconciliationSnapshot(
                deploymentId,
                resource.TenantId,
                RedisResourceDeployment.ResourceType,
                resource.Id,
                resource.ResourceGroupId,
                scopeId,
                cellId,
                1,
                1,
                1,
                DateTime.UtcNow,
                new ResourceDeploymentResolvedPlacementSnapshot(targetId, 1, NamespaceName, null, storage, compute)),
            1,
            1);
        var manifest = resource.ToKubernetes(NamespaceName, storage, compute, identity);
        Assert.IsType<RedisKubernetesApplied>((await RedisKubernetesResourceManager.ApplyAsync(client,
            manifest,
            RedisResourceDeploymentMetadata.Create(deploymentId, cellId, targetId, 1, DateTime.UtcNow),
            deadline.Token)).Value);
        var controller =
            await client.AppsV1.ReadNamespacedStatefulSetAsync(resource.Name,
                NamespaceName,
                cancellationToken: deadline.Token);
        var binding = controller.Metadata.Uid;
        AssertOwned(controller.Metadata, identity);
        var provider = new ExactClient(client, targetId);
        var observer = new RedisResourceDeploymentObserver((tenant, id, _) =>
            {
                Assert.Equal(resource.TenantId, tenant);
                Assert.Equal(resource.Id, id);
                return Task.FromResult<Redis?>(resource);
            },
            provider);
        ResourceDeploymentBoundObservation? ready = null;
        var until = Stopwatch.StartNew();
        while (until.Elapsed < TimeSpan.FromMinutes(3))
        {
            ready = await observer.ObserveBoundAsync(context, binding, deadline.Token);
            if (ready.Observation is ObservedWorkload { Ready: true })
                break;
            await Task.Delay(1000, deadline.Token);
        }

        Assert.True(ready?.Observation is ObservedWorkload { Ready: true });
        var instance = Assert.Single(Assert.IsType<AvailableWorkloadDiagnostics>(ready!.Diagnostics).Instances);
        var pod = await client.CoreV1.ReadNamespacedPodAsync(instance.Name,
            NamespaceName,
            cancellationToken: deadline.Token);
        Assert.Equal(instance.Uid, pod.Metadata.Uid);
        AssertOwned(pod.Metadata, identity);
        Assert.True(KubernetesOwnedWatchItem.ControlledBy(pod.Metadata, binding, "StatefulSet", "apps/v1"));
        var service = await client.CoreV1.ReadNamespacedServiceAsync(RedisInKubernetes.BuildServiceName(resource.Name),
            NamespaceName,
            cancellationToken: deadline.Token);
        AssertOwned(service.Metadata, identity);
        var slices = await client.DiscoveryV1.ListNamespacedEndpointSliceAsync(NamespaceName,
            labelSelector: $"kubernetes.io/service-name={service.Metadata.Name}",
            cancellationToken: deadline.Token);
        Assert.NotEmpty(slices.Items);
        Assert.All(slices.Items,
            slice => Assert.True(
                KubernetesOwnedWatchItem.ControlledBy(slice.Metadata, service.Metadata.Uid, "Service", "v1")));
        var source = new RedisResourceDeploymentChangeSource((_, _, _) => Task.FromResult<Redis?>(resource),
            provider,
            (id, _) => Task.FromResult<LookupDeploymentTargetOutcome>(TargetDto(id)),
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
            var guardedController = await client.AppsV1.ReadNamespacedStatefulSetAsync(resource.Name,
                NamespaceName,
                cancellationToken: deadline.Token);
            Assert.Equal(binding, guardedController.Metadata.Uid);
            AssertOwned(guardedController.Metadata, identity);
            var freshPod =
                await client.CoreV1.ReadNamespacedPodAsync(instance.Name,
                    NamespaceName,
                    cancellationToken: deadline.Token);
            Assert.Equal(instance.Uid, freshPod.Metadata.Uid);
            AssertOwned(freshPod.Metadata, identity);
            Assert.True(KubernetesOwnedWatchItem.ControlledBy(freshPod.Metadata, binding, "StatefulSet", "apps/v1"));
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
            Assert.True(latency.Elapsed <= TimeSpan.FromSeconds(5)); // One native boundary sample, not a p95/UI claim.
            Assert.Equal(binding, changes.Current.BindingId);
            var postDelete = await observer.ObserveBoundAsync(context, binding, deadline.Token);
            if (postDelete.Diagnostics is AvailableWorkloadDiagnostics after)
                Assert.DoesNotContain(after.Instances, item => item.Uid == instance.Uid);
            var freshController = await client.AppsV1.ReadNamespacedStatefulSetAsync(resource.Name,
                NamespaceName,
                cancellationToken: deadline.Token);
            Assert.Equal(binding, freshController.Metadata.Uid);
            AssertOwned(freshController.Metadata, identity);
            await client.AppsV1.PatchNamespacedStatefulSetAsync(new V1Patch(JsonSerializer.Serialize(new
            {
                metadata = new
                {
                    uid = binding,
                    labels = new Dictionary<string, string>
                    {
                        ["pole.io/resource-id"] = "observation-test-foreign"
                    }
                }
            }),
                    V1Patch.PatchType.MergePatch),
                resource.Name,
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

        // Dedicated target client remains owned by this test after every source stream is disposed.
        Assert.Equal(binding,
            (await client.AppsV1.ReadNamespacedStatefulSetAsync(resource.Name,
                NamespaceName,
                cancellationToken: deadline.Token)).Metadata.Uid);
        // KubernetesIntegrationTestBase checks namespace UID/run/worktree ownership and removes only this test's namespace/PVCs.
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
        "Healthy");

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

    private sealed class Budget : IResourceDeploymentObservationReadBudget
    {
        public Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct = default) => read(ct);
    }
}
