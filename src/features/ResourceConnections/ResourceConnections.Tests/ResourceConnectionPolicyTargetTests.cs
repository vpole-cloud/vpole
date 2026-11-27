// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Net;
using System.Text;
using k8s;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Kubernetes;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.ResourceConnections.Tests;

public sealed class ResourceConnectionPolicyTargetTests
{
    [Fact]
    public async Task PausedOldCreate_AfterDeleteAndTombstone_IsCompensatedByExactUidAndVersion()
    {
        using var fixture = new Fixture();
        fixture.Api.DelayNextCreate = true;
        var delayed = fixture.Target.ApplyAsync(fixture.Connection, fixture.Snapshot);
        await fixture.Api.CreatePaused.Task.WaitAsync(TimeSpan.FromSeconds(10));

        fixture.Tombstone = new ResourceConnectionCleanupTombstone(
            fixture.Connection.Id,
            fixture.Connection.TenantId,
            fixture.Connection.Generation + 1,
            ImmutableList.Create(fixture.Snapshot),
            DateTime.UtcNow);
        fixture.ConnectionInStore = null;
        Assert.IsType<ResourceConnectionPolicySucceeded>(
            (await fixture.Target.DeleteTombstoneAsync(fixture.Tombstone, fixture.Snapshot)).Value);
        Assert.Null(fixture.Api.Current);

        fixture.Api.ReleaseCreate.SetResult();
        Assert.IsType<ResourceConnectionPolicyDesiredStateChanged>((await delayed).Value);
        Assert.Null(fixture.Api.Current);
        Assert.Equal("policy-uid", fixture.Api.DeleteOptions?.Preconditions.Uid);
        Assert.Equal("1", fixture.Api.DeleteOptions?.Preconditions.ResourceVersion);
    }

    [Fact]
    public async Task TombstoneRecovery_RemovesPolicyWrittenAfterPublicConnectionWasPurged()
    {
        using var fixture = new Fixture();
        Assert.IsType<ResourceConnectionPolicySucceeded>(
            (await fixture.Target.ApplyAsync(fixture.Connection, fixture.Snapshot)).Value);
        Assert.NotNull(fixture.Api.Current);
        fixture.Tombstone = new ResourceConnectionCleanupTombstone(
            fixture.Connection.Id,
            fixture.Connection.TenantId,
            fixture.Connection.Generation + 1,
            ImmutableList.Create(fixture.Snapshot),
            DateTime.UtcNow);
        fixture.ConnectionInStore = null;

        var result = await fixture.Target.DeleteTombstoneAsync(fixture.Tombstone, fixture.Snapshot);

        Assert.IsType<ResourceConnectionPolicySucceeded>(result.Value);
        Assert.Null(fixture.Api.Current);
        Assert.NotNull(fixture.Api.DeleteOptions?.Preconditions.ResourceVersion);
    }

    [Fact]
    public async Task AppliedPolicyRead_DetectsMissingOrDriftedManifestAndRepairsIt()
    {
        using var fixture = new Fixture();
        Assert.IsType<ResourceConnectionPolicySucceeded>(
            (await fixture.Target.ApplyAsync(fixture.Connection, fixture.Snapshot)).Value);
        Assert.IsType<ResourceConnectionPolicySucceeded>(
            (await fixture.Target.ApplyAsync(fixture.Connection, fixture.Snapshot)).Value);
        Assert.Equal(1, fixture.Api.CreateCount);
        Assert.Equal(0, fixture.Api.ReplaceCount);

        fixture.Api.Current!.Spec.Ingress[0].Ports[0].Port = 9090;
        Assert.IsType<ResourceConnectionPolicySucceeded>(
            (await fixture.Target.ApplyAsync(fixture.Connection, fixture.Snapshot)).Value);
        Assert.Equal(1, fixture.Api.ReplaceCount);
        Assert.Equal("8080", fixture.Api.Current.Spec.Ingress[0].Ports[0].Port.Value);

        fixture.Api.Current.Spec.Ingress[0].Ports[0].EndPort = 9090;
        Assert.IsType<ResourceConnectionPolicySucceeded>(
            (await fixture.Target.ApplyAsync(fixture.Connection, fixture.Snapshot)).Value);
        Assert.Equal(2, fixture.Api.ReplaceCount);
        Assert.Null(fixture.Api.Current.Spec.Ingress[0].Ports[0].EndPort);

        fixture.Api.Current = null;
        Assert.IsType<ResourceConnectionPolicySucceeded>(
            (await fixture.Target.ApplyAsync(fixture.Connection, fixture.Snapshot)).Value);
        Assert.Equal(2, fixture.Api.CreateCount);
    }

    [Fact]
    public async Task TargetUnavailableBaseline_PreservesTargetFailureReason()
    {
        using var fixture = new Fixture();
        fixture.BaselineOutcome = new RuntimeCellIngressBaselineTargetDisabled(
            fixture.Snapshot.DestinationRuntimeCellId);

        var result = await fixture.Target.ApplyAsync(fixture.Connection, fixture.Snapshot);

        Assert.IsType<ResourceConnectionPolicyTargetUnavailable>(result.Value);
        Assert.Equal(0, fixture.Api.CreateCount);
    }

    [Fact]
    public async Task BaselineNotReady_PreservesBaselineFailureReason()
    {
        using var fixture = new Fixture();
        fixture.BaselineOutcome = new RuntimeCellIngressBaselineNotReady(fixture.Snapshot.DestinationRuntimeCellId);

        var result = await fixture.Target.ApplyAsync(fixture.Connection, fixture.Snapshot);

        Assert.IsType<ResourceConnectionPolicyBaselineUnavailable>(result.Value);
        Assert.Equal(0, fixture.Api.CreateCount);
    }

    [Fact]
    public async Task CompletedRetiredLocation_StillDeletesLateOldPolicy()
    {
        using var fixture = new Fixture();
        Assert.IsType<ResourceConnectionPolicySucceeded>(
            (await fixture.Target.ApplyAsync(fixture.Connection, fixture.Snapshot)).Value);
        var movedSnapshot = fixture.Snapshot with
        {
            DestinationRuntimeCellId = "new-destination-cell",
            DestinationNamespace = "new-destination-namespace",
        };
        var moved = fixture.Connection.RefreshResolvedTargets([movedSnapshot]);
        var completed = Assert.IsType<ConnectionCleanupRecorded>(
                moved.RecordCleanupCompleted(fixture.Snapshot.PolicyLocation, moved.Generation, DateTime.UtcNow).Value)
            .Connection;
        fixture.ConnectionInStore = completed;

        var deleted = await fixture.Target.DeleteAsync(completed, fixture.Snapshot);

        Assert.IsType<ResourceConnectionPolicySucceeded>(deleted.Value);
        Assert.Null(fixture.Api.Current);
        Assert.NotNull(fixture.Api.DeleteOptions?.Preconditions.ResourceVersion);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly PolicyApi Api = new();
        public readonly ResourceConnection Connection;
        public readonly ConnectionTargetSnapshot Snapshot;
        private readonly Kubernetes client;
        private readonly ServiceProvider services;

        public EnsureRuntimeCellIngressBaselineOutcome BaselineOutcome = new RuntimeCellIngressBaselineReady();

        public ResourceConnection? ConnectionInStore;
        public ResourceConnectionCleanupTombstone? Tombstone;

        public Fixture()
        {
            Snapshot = ConnectionTargetSnapshot.Create(
                "target",
                "destination-deployment",
                "destination-cell",
                "destination-namespace",
                "receiver-svc",
                80,
                8080,
                [new ConnectionSourcePlacement("source-deployment", "source-cell", "source-namespace")]);
            Connection = ResourceConnectionFactory.Create(
                ObjectId.GenerateNewId().ToString(),
                "tenant",
                "caller-to-receiver",
                ResourceTypeId.Create("pole/web-service"),
                ObjectId.GenerateNewId().ToString(),
                ResourceTypeId.Create("pole/web-service"),
                ObjectId.GenerateNewId().ToString(),
                "http",
                [Snapshot]);
            ConnectionInStore = Connection;
            client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://kubernetes.test" }, Api);
            var collection = new ServiceCollection();
            collection.AddResourceConnectionsFeature();
            collection.AddSingleton<IKubernetesClientProvider>(new ClientProvider(client));
            collection.AddScoped<EnsureRuntimeCellIngressBaseline>(_ =>
                (request, ct) => Task.FromResult(BaselineOutcome));
            collection.AddScoped<LoadResourceConnection>(_ => (
                tenantId, connectionId,
                ct) => Task.FromResult<ResourceConnection?>(ConnectionInStore));
            collection.AddScoped<LoadResourceConnectionTombstone>(_ => (
                tenantId, connectionId,
                ct) => Task.FromResult(Tombstone));
            services = collection.BuildServiceProvider();
        }

        public IResourceConnectionPolicyTarget Target => services.GetRequiredService<IResourceConnectionPolicyTarget>();

        public void Dispose()
        {
            services.Dispose();
            client.Dispose();
        }
    }

    private sealed class ClientProvider(
        IKubernetes client
    ) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string targetId, CancellationToken ct = default)
        {
            Assert.Equal("target", targetId);
            return Task.FromResult(client);
        }

        public Task<IKubernetes> GetForCleanupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) => GetAsync(deploymentTargetId, cancellationToken);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("A connection must use its target ID.");
    }

    private sealed class PolicyApi : DelegatingHandler
    {
        public readonly TaskCompletionSource CreatePaused = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public readonly TaskCompletionSource ReleaseCreate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CreateCount;
        public V1NetworkPolicy? Current;
        public bool DelayNextCreate;
        public V1DeleteOptions? DeleteOptions;
        public int ReplaceCount;
        private int version;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return Response(
                    request,
                    Current is null ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                    Current is null ? "{}" : KubernetesJson.Serialize(Current));
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (request.Method == HttpMethod.Delete)
            {
                DeleteOptions = KubernetesJson.Deserialize<V1DeleteOptions>(body);
                if (Current is null)
                    return Response(request, HttpStatusCode.NotFound, "{}");
                if (DeleteOptions.Preconditions.Uid != Current.Metadata.Uid ||
                    DeleteOptions.Preconditions.ResourceVersion != Current.Metadata.ResourceVersion)
                    return Response(request, HttpStatusCode.Conflict, "{}");
                Current = null;
                return Response(request, HttpStatusCode.OK, "{}");
            }

            var next = KubernetesJson.Deserialize<V1NetworkPolicy>(body);
            if (request.Method == HttpMethod.Post)
            {
                CreateCount++;
                if (DelayNextCreate)
                {
                    DelayNextCreate = false;
                    CreatePaused.SetResult();
                    await ReleaseCreate.Task.WaitAsync(cancellationToken);
                }

                if (Current is not null)
                    return Response(request, HttpStatusCode.Conflict, "{}");
            }
            else if (request.Method == HttpMethod.Put)
            {
                ReplaceCount++;
                if (next.Metadata.ResourceVersion != Current?.Metadata.ResourceVersion)
                    return Response(request, HttpStatusCode.Conflict, "{}");
            }

            next.Metadata.Uid = "policy-uid";
            next.Metadata.ResourceVersion = (++version).ToString();
            Current = next;
            return Response(request, HttpStatusCode.OK, KubernetesJson.Serialize(next));
        }

        private static HttpResponseMessage Response(
            HttpRequestMessage request,
            HttpStatusCode status,
            string body) => new(status)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
