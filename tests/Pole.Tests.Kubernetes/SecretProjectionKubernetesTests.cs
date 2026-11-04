using System.Net;
using System.Text;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Domain;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.Kubernetes;

[Collection(KubernetesLabCollection.Name)]
[Trait(TestTraits.Layer, TestTraits.Layers.Kubernetes)]
[Trait(TestTraits.Concern, "Secrets")]
[Trait(TestTraits.Scope, TestTraits.Scopes.SingleCluster)]
[Trait(TestTraits.Feature, "Secrets")]
public sealed class SecretProjectionKubernetesTests(KubernetesClusterFixture fixture)
{
    [KubernetesFact("api")]
    public async Task Native_projection_rotates_repairs_drift_and_recreation_then_deletes_after_revocation()
    {
        var namespaceName = fixture.Identity.CreateNamespaceName("secret-projection");
        await using var resources = new TestResourceScope("native Secret projection");
        await OwnedNamespaceManager.CreateTrackedAsync(resources.CreateConcurrentCleanupGroup("owned namespace"),
            fixture.Client, namespaceName, fixture.Identity);
        var tenantId = ObjectId.GenerateNewId().ToString();
        var deploymentId = ObjectId.GenerateNewId().ToString();
        var binding = SecretBinding.Create(ObjectId.GenerateNewId().ToString(), "pole/postgresql", ObjectId.GenerateNewId().ToString(), new AllSecretDeployments(), []);
        var secret = SecretFactory.CreateManaged(ObjectId.GenerateNewId().ToString(), tenantId, ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(), "credentials", "live projection", ["username", "password"], binding, KubernetesSecretType.BasicAuthentication, DateTime.UtcNow);
        SecretProjection Projection() => SecretProjectionFactory.Create(secret, binding, deploymentId, "cell", "selected-target", namespaceName);
        var services = new ServiceCollection().AddSecretsFeature();
        services.AddSingleton<IKubernetesClientProvider>(new SelectedClient(fixture.Client));
        services.AddSingleton<LoadSecretById>((_, _, _) => Task.FromResult<Secret?>(secret));
        services.AddSingleton<LoadExactResourceDeploymentRuntimePlacement>((_, _, _, _, _) => Task.FromResult<ResourceDeploymentRuntimePlacementDto?>(
            new(deploymentId, binding.ConsumerId, "cell", "selected-target", namespaceName, 1, 1, null!, ResourceDeploymentStatus.Pending, ResourceDeploymentDesiredOperation.Apply, secret.IsolationScopeId)));
        using var provider = services.BuildServiceProvider();
        var target = provider.GetRequiredService<ISecretProjectionTarget>();
        async Task Apply(string password)
        {
            var projection = Projection();
            var applied = await target.ApplyAsync(projection, _ => Task.FromResult<SecretProjectionManifestOutcome>(SecretProjectionManifest.Create(projection, binding,
                new Dictionary<string, string> { ["username"] = "test-user", ["password"] = password })), default);
            Xunit.Assert.IsType<SecretProjectionTargetSucceeded>(applied.Value);
        }
        await Apply("initial-password");
        var initial = await fixture.Client.CoreV1.ReadNamespacedSecretAsync(Projection().KubernetesName, namespaceName);
        Assert.Equal("kubernetes.io/basic-auth", initial.Type);
        Assert.Equal("initial-password", Encoding.UTF8.GetString(initial.Data["password"]));
        secret = secret.Rotate(["username", "password"], DateTime.UtcNow);
        await Apply("rotated-password");
        var rotated = await fixture.Client.CoreV1.ReadNamespacedSecretAsync(Projection().KubernetesName, namespaceName);
        Assert.Equal(initial.Metadata.Uid, rotated.Metadata.Uid);
        Assert.Equal("rotated-password", Encoding.UTF8.GetString(rotated.Data["password"]));
        rotated.Data["password"] = Encoding.UTF8.GetBytes("drifted-password");
        rotated.Metadata.Annotations["pole.io/secret-revision"] = "999999999";
        await fixture.Client.CoreV1.ReplaceNamespacedSecretAsync(rotated, rotated.Metadata.Name, namespaceName);
        await Apply("rotated-password");
        var repaired = await fixture.Client.CoreV1.ReadNamespacedSecretAsync(Projection().KubernetesName, namespaceName);
        Assert.Equal("rotated-password", Encoding.UTF8.GetString(repaired.Data["password"]));
        Assert.Equal(secret.Revision.ToString(), repaired.Metadata.Annotations["pole.io/secret-revision"]);
        await fixture.Client.CoreV1.DeleteNamespacedSecretAsync(repaired.Metadata.Name, namespaceName);
        await Apply("rotated-password");
        var recreated = await fixture.Client.CoreV1.ReadNamespacedSecretAsync(Projection().KubernetesName, namespaceName);
        Assert.NotEqual(initial.Metadata.Uid, recreated.Metadata.Uid);
        var revoked = Projection();
        secret = secret.Detach(binding.Id, DateTime.UtcNow);
        Assert.IsType<SecretProjectionTargetSucceeded>((await target.DeleteAsync(revoked, default)).Value);
        var missing = await Assert.ThrowsAsync<HttpOperationException>(() => fixture.Client.CoreV1.ReadNamespacedSecretAsync(revoked.KubernetesName, namespaceName));
        Assert.Equal(HttpStatusCode.NotFound, missing.Response.StatusCode);
    }

    private sealed class SelectedClient(IKubernetes client) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string id, CancellationToken ct = default)
        {
            Assert.Equal("selected-target", id);
            return Task.FromResult(client);
        }
        public Task<IKubernetes> GetForCleanupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            GetAsync(deploymentTargetId, cancellationToken);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken ct = default) => throw new InvalidOperationException("Ambient target is forbidden.");
    }
}
