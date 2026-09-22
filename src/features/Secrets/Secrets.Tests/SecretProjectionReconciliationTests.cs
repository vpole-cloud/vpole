using System.Collections.Immutable;
using System.Text.Json;
using k8s.Models;
using Pole.Encryption;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Domain;
using Pole.ResultType;
using Xunit;

namespace Pole.Features.Secrets.Tests;

public sealed class SecretProjectionReconciliationTests
{
    [Fact]
    public async Task Partial_failure_is_independent_and_deleted_projection_is_repaired()
    {
        var fixture = new Fixture();
        fixture.Target.Unavailable.Add("target-b");
        await fixture.Run();
        Assert.IsType<ReadySecretProjection>(fixture.Rows["deployment-a"].State);
        Assert.IsType<FailedSecretProjection>(fixture.Rows["deployment-b"].State);
        Assert.Single(fixture.Target.Manifests);
        fixture.Target.Unavailable.Clear();
        fixture.Target.Manifests.Clear();
        await fixture.Run();
        Assert.Equal(2, fixture.Target.Manifests.Count);
        Assert.All(fixture.Rows.Values, row => Assert.Equal(1, row.AppliedVersion));
        Assert.Equal("namespace-a", fixture.Target.Manifests["deployment-a"].Metadata.NamespaceProperty);
        Assert.Equal("namespace-b", fixture.Target.Manifests["deployment-b"].Metadata.NamespaceProperty);
    }

    [Fact]
    public async Task Rotation_converges_and_revocation_during_write_cleans_only_its_consumer()
    {
        var fixture = new Fixture();
        await fixture.Run();
        fixture.Secret = fixture.Secret.Rotate(["password"], DateTime.UtcNow);
        await fixture.Run();
        Assert.All(fixture.Rows.Values, row => Assert.Equal(2, row.AppliedVersion));
        fixture.Target.AfterApply = row =>
        {
            if (row.DeploymentId == "deployment-a")
                fixture.Secret = fixture.Secret.Detach("binding-a", DateTime.UtcNow);
        };
        await fixture.Run();
        Assert.False(fixture.Target.Manifests.ContainsKey("deployment-a"));
        Assert.True(fixture.Target.Manifests.ContainsKey("deployment-b"));
        Assert.IsType<DeletedSecretProjection>(fixture.Rows["deployment-a"].State);
        await fixture.Run();
        Assert.False(fixture.Target.Manifests.ContainsKey("deployment-a"));
    }

    [Fact]
    public async Task Removed_deployment_and_foreign_scope_never_receive_values()
    {
        var fixture = new Fixture();
        await fixture.Run();
        fixture.Removed.Add("deployment-a");
        fixture.ForeignScope.Add("deployment-b");
        await fixture.Run();
        Assert.Empty(fixture.Target.Manifests);
        Assert.All(fixture.Rows.Values, row => Assert.IsType<DeletedSecretProjection>(row.State));
    }

    private sealed class Fixture
    {
        public Secret Secret = SecretFactory.Create("secret", "tenant", "group", "scope", "credentials", "tests", ["password"], DateTime.UtcNow)
            .Bind(SecretBinding.Create("binding-a", "pole/postgresql", "resource-a", new AllSecretDeployments(), []), DateTime.UtcNow)
            .Bind(SecretBinding.Create("binding-b", "pole/postgresql", "resource-b", new AllSecretDeployments(), []), DateTime.UtcNow);
        public readonly Dictionary<string, SecretProjection> Rows = [];
        public readonly HashSet<string> Removed = [];
        public readonly HashSet<string> ForeignScope = [];
        public readonly Target Target = new();

        public Task Run() => new SecretProjectionReconciler(
            (_, _, _) => Task.FromResult<Secret?>(Secret),
            (_, _, version, _) => Task.FromResult<SecretVersion?>(new("version", "tenant", "secret", version, null!, DateTime.UtcNow)),
            (_, _, _) => Task.FromResult<IReadOnlyList<SecretProjection>>(Rows.Values.ToArray()),
            (_, _, resource, _) => Task.FromResult<IReadOnlyList<PlacementSummaryDto>>([
                new("deployment-" + resource[^1], "scope", "cell", "target-" + resource[^1], 1, 0, "Pending", null, null!, [])]),
            (_, _, resource, deployment, _) => Task.FromResult<ResourceDeploymentRuntimePlacementDto?>(Removed.Contains(deployment) ? null :
                new(deployment, resource, "cell", "target-" + resource[^1], "namespace-" + resource[^1], 1, null!, "Pending", "Apply", ForeignScope.Contains(deployment) ? "foreign" : "scope")),
            (desired, claim, now, _) => Task.FromResult<SecretProjection?>(desired.Claim(claim, now)),
            (row, _) => { Rows[row.DeploymentId] = row; return Task.CompletedTask; },
            new Encryption(), Target, TimeProvider.System).ReconcileAsync("tenant", "secret", default);
    }

    private sealed class Target : ISecretProjectionTarget
    {
        public readonly HashSet<string> Unavailable = [];
        public readonly Dictionary<string, V1Secret> Manifests = [];
        public Action<SecretProjection>? AfterApply;

        public async Task ApplyAsync(SecretProjection projection, Func<CancellationToken, Task<V1Secret>> createManifest, CancellationToken ct)
        {
            if (Unavailable.Contains(projection.DeploymentTargetId))
                throw new SecretProjectionTargetUnavailableException();
            var manifest = await createManifest(ct);
            Manifests[projection.DeploymentId] = manifest;
            AfterApply?.Invoke(projection);
            foreach (var bytes in manifest.Data.Values)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }

        public Task DeleteAsync(SecretProjection projection, CancellationToken ct)
        {
            Manifests.Remove(projection.DeploymentId);
            return Task.CompletedTask;
        }
    }

    private sealed class Encryption : IEnvelopeEncryptionService
    {
        public EnvelopeEncryptionKeyReference ActiveKey => throw new NotSupportedException();
        public ValueTask<Result<byte[], EnvelopeDecryptionFailed>> DecryptAsync(EncryptedValue value, EnvelopeEncryptionContext context, CancellationToken ct = default) =>
            ValueTask.FromResult<Result<byte[], EnvelopeDecryptionFailed>>(new Ok<byte[]>(JsonSerializer.SerializeToUtf8Bytes(new { password = "private-value" })));
        public ValueTask<Result<EncryptedValue, EnvelopeEncryptionFailed>> EncryptAsync(ReadOnlyMemory<byte> value, EnvelopeEncryptionContext context, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<Result<EncryptedValue, EnvelopeEncryptionFailed>> EncryptStringAsync(string value, EnvelopeEncryptionContext context, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<Result<string, EnvelopeDecryptionFailed>> DecryptStringAsync(EncryptedValue value, EnvelopeEncryptionContext context, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<Result<EncryptedValue, EnvelopeRewrapFailed>> RewrapAsync(EncryptedValue value, EnvelopeEncryptionContext context, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
