using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Domain;
using Xunit;

namespace Pole.Features.Secrets.Tests;

public sealed class SecretProjectionTargetTests
{
    [Fact]
    public async Task Delayed_old_write_cannot_overwrite_renewed_owner_and_metadata_drift_is_repaired()
    {
        using var fixture = new Fixture();
        var old = fixture.Projection;
        await fixture.Target.ApplyAsync(old, fixture.Manifest("old"), default);
        fixture.Api.DelayNextPut = true;
        var delayed = fixture.Target.ApplyAsync(old, fixture.Manifest("old-delayed"), default);
        await fixture.Api.PutDelayed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Secret = fixture.Secret.Rotate(["password"], DateTime.UtcNow);
        await fixture.Target.ApplyAsync(fixture.Projection, fixture.Manifest("new"), default);
        fixture.Api.ReleasePut.SetResult();
        await Assert.ThrowsAsync<SecretProjectionTargetUnavailableException>(() => delayed);
        Assert.Equal("new", Encoding.UTF8.GetString(fixture.Api.Current!.Data["password"]));
        await Assert.ThrowsAsync<SecretProjectionTargetUnavailableException>(() => fixture.Target.ApplyAsync(old, _ => Task.FromResult(SecretProjectionManifest.Create(old, fixture.Binding, new Dictionary<string, string> { ["password"] = "stale" })), default));
        fixture.Api.Current.Metadata.Annotations["pole.io/secret-revision"] = "999999999";
        await fixture.Target.ApplyAsync(fixture.Projection, fixture.Manifest("repaired"), default);
        Assert.Equal(fixture.Secret.Revision.ToString(), fixture.Api.Current.Metadata.Annotations["pole.io/secret-revision"]);
    }

    [Fact]
    public async Task Cleanup_preserves_current_binding_and_revoked_delete_uses_uid_and_resource_version()
    {
        using var fixture = new Fixture();
        var old = fixture.Projection;
        await fixture.Target.ApplyAsync(old, fixture.Manifest("value"), default);
        await fixture.Target.DeleteAsync(old, default);
        Assert.Null(fixture.Api.DeleteOptions);
        fixture.Secret = fixture.Secret.Detach(fixture.Binding.Id, DateTime.UtcNow);
        await fixture.Target.DeleteAsync(old, default);
        Assert.Equal("owned-uid", fixture.Api.DeleteOptions!.Preconditions.Uid);
        Assert.Equal("1", fixture.Api.DeleteOptions.Preconditions.ResourceVersion);
        Assert.Null(fixture.Api.Current);
    }

    [Fact]
    public async Task Sdk_exception_body_is_discarded_but_provider_bug_propagates()
    {
        using var fixture = new Fixture();
        fixture.Api.FailWrites = true;
        var error = await Assert.ThrowsAsync<SecretProjectionTargetUnavailableException>(() => fixture.Target.ApplyAsync(fixture.Projection, fixture.Manifest("private-password"), default));
        Assert.Equal(SecretProjectionTargetFailure.RequestRejected, error.Reason);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-password", error.ToString());
        fixture.Provider.Failure = new DeploymentTargetUnavailableException("target", DeploymentTargetUnavailableReason.Disabled);
        await Assert.ThrowsAsync<SecretProjectionTargetUnavailableException>(() => fixture.Target.ApplyAsync(fixture.Projection, fixture.Manifest("value"), default));
        fixture.Provider.Failure = new InvalidOperationException("provider-bug");
        var bug = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Target.ApplyAsync(fixture.Projection, fixture.Manifest("value"), default));
        Assert.Equal("provider-bug", bug.Message);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly SecretBinding Binding = SecretBinding.Create("binding", "pole/postgresql", "resource", new AllSecretDeployments(), []);
        public Secret Secret;
        public readonly SecretApi Api = new();
        public readonly ClientProvider Provider;
        private readonly ServiceProvider services;
        public ISecretProjectionTarget Target => services.GetRequiredService<ISecretProjectionTarget>();
        public SecretProjection Projection => SecretProjectionFactory.Create(Secret, Binding, "deployment", "cell", "target", "namespace");

        public Fixture()
        {
            Secret = SecretFactory.Create("secret", "tenant", "group", "scope", "credentials", "test", ["password"], DateTime.UtcNow).Bind(Binding, DateTime.UtcNow);
            Provider = new ClientProvider(new Kubernetes(new KubernetesClientConfiguration { Host = "http://kubernetes.test" }, Api));
            var collection = new ServiceCollection().AddSecretsFeature();
            collection.AddSingleton<IKubernetesClientProvider>(Provider);
            collection.AddSingleton<LoadSecretById>((_, _, _) => Task.FromResult<Secret?>(Secret));
            collection.AddSingleton<LoadExactResourceDeploymentRuntimePlacement>((_, _, _, _, _) => Task.FromResult<ResourceDeploymentRuntimePlacementDto?>(
                new("deployment", "resource", "cell", "target", "namespace", 1, null!, "Pending", "Apply", "scope")));
            services = collection.BuildServiceProvider();
        }

        public Func<CancellationToken, Task<V1Secret>> Manifest(string password) => _ => Task.FromResult(SecretProjectionManifest.Create(Projection, Binding, new Dictionary<string, string> { ["password"] = password }));
        public void Dispose() { services.Dispose(); Provider.Client.Dispose(); }
    }

    private sealed class ClientProvider(IKubernetes client) : IKubernetesClientProvider
    {
        public IKubernetes Client => client;
        public Exception? Failure;
        public Task<IKubernetes> GetAsync(string id, CancellationToken ct = default)
        {
            Assert.Equal("target", id);
            return Failure is null ? Task.FromResult(client) : Task.FromException<IKubernetes>(Failure);
        }
        public Task<IKubernetes> GetDefaultAsync(CancellationToken ct = default) => throw new InvalidOperationException("Default target must not be used.");
    }

    private sealed class SecretApi : DelegatingHandler
    {
        public V1Secret? Current;
        public V1DeleteOptions? DeleteOptions;
        public bool DelayNextPut;
        public bool FailWrites;
        public readonly TaskCompletionSource PutDelayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleasePut = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int revision;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
                return Response(request, Current is null ? HttpStatusCode.NotFound : HttpStatusCode.OK, Current is null ? "{}" : KubernetesJson.Serialize(Current));
            var body = await request.Content!.ReadAsStringAsync(ct);
            if (FailWrites)
                return Response(request, HttpStatusCode.BadRequest, body);
            if (request.Method == HttpMethod.Delete)
            {
                DeleteOptions = KubernetesJson.Deserialize<V1DeleteOptions>(body);
                Current = null;
                return Response(request, HttpStatusCode.OK, "{}");
            }
            var next = KubernetesJson.Deserialize<V1Secret>(body);
            if (request.Method == HttpMethod.Put)
            {
                if (DelayNextPut)
                {
                    DelayNextPut = false;
                    PutDelayed.SetResult();
                    await ReleasePut.Task.WaitAsync(ct);
                }
                if (next.Metadata.ResourceVersion != Current?.Metadata.ResourceVersion)
                    return Response(request, HttpStatusCode.Conflict, "{}");
            }
            next.Metadata.Uid = "owned-uid";
            next.Metadata.ResourceVersion = (++revision).ToString();
            Current = next;
            return Response(request, HttpStatusCode.OK, KubernetesJson.Serialize(next));
        }

        private static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode code, string body) =>
            new(code) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
