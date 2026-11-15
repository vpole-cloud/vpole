using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Pole.Encryption;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Primitives;
using Xunit;
using k8s.Models;

namespace Pole.Features.Secrets.Tests;

public sealed class SecretProjectionReconciliationTests
{
    [Fact]
    public async Task Owner_only_credentials_project_only_to_owner_and_revoke_stale_foreign_bindings()
    {
        var fixture = new Fixture();
        await fixture.Run();
        Assert.Equal(2, fixture.Target.Manifests.Count);
        fixture.Secret = fixture.Secret with
        {
            Management = new ProviderManagedSecret(
                "pole/postgresql",
                "resource-a",
                "admin",
                "binding-a",
                KubernetesSecretType.Opaque,
                ManagedSecretSharing.OwnerOnly),
        };
        await fixture.Run();
        Assert.Single(fixture.Target.Manifests);
        Assert.True(fixture.Target.Manifests.ContainsKey("deployment-a"));
        Assert.False(fixture.Target.Manifests.ContainsKey("deployment-b"));
        Assert.IsType<ReadySecretProjection>(fixture.Rows["deployment-a"].State);
        Assert.IsType<DeletedSecretProjection>(fixture.Rows["deployment-b"].State);
    }

    [Fact]
    public async Task Bootstrap_grants_never_create_native_projections()
    {
        var fixture = new Fixture();
        fixture.Secret = fixture.Secret with
        {
            Bindings = fixture.Secret
                .Bindings
                .Select(binding => binding with
                {
                    Usage = SecretBindingUsage.Bootstrap,
                })
                .ToImmutableArray(),
        };
        await fixture.Run();
        Assert.Empty(fixture.Rows);
        Assert.Empty(fixture.Target.Manifests);
    }

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

    [Fact]
    public async Task Failed_cleanup_remains_retryable_until_target_recovers()
    {
        var fixture = new Fixture();
        await fixture.Run();
        fixture.Secret = fixture.Secret.Detach("binding-a", DateTime.UtcNow);
        fixture.Target.Unavailable.Add("target-a");
        await fixture.Run();
        Assert.IsType<FailedSecretProjection>(fixture.Rows["deployment-a"].State);
        Assert.True(fixture.Target.Manifests.ContainsKey("deployment-a"));
        fixture.Target.Unavailable.Clear();
        await fixture.Run();
        Assert.IsType<DeletedSecretProjection>(fixture.Rows["deployment-a"].State);
        Assert.False(fixture.Target.Manifests.ContainsKey("deployment-a"));
    }

    [Fact]
    public async Task Decryption_failure_is_safe_status_and_retry_recovers()
    {
        var fixture = new Fixture();
        fixture.Encryption.Fail = true;
        await fixture.Run();
        Assert.Empty(fixture.Target.Manifests);
        Assert.All(
            fixture.Rows.Values,
            row => Assert.Equal("DecryptionUnavailable", Assert.IsType<FailedSecretProjection>(row.State).FailureCode));
        fixture.Encryption.Fail = false;
        await fixture.Run();
        Assert.All(fixture.Rows.Values, row => Assert.IsType<ReadySecretProjection>(row.State));
    }

    private sealed class Fixture
    {
        public readonly Encryption Encryption = new();
        public readonly HashSet<string> ForeignScope = [];
        public readonly HashSet<string> Removed = [];
        public readonly Dictionary<string, SecretProjection> Rows = [];
        public readonly Target Target = new();

        public Secret Secret = SecretFactory
            .Create("secret", "tenant", "group", "scope", "credentials", "tests", ["password"], DateTime.UtcNow)
            .Bind(
                SecretBinding.Create("binding-a", "pole/postgresql", "resource-a", new AllSecretDeployments(), []),
                DateTime.UtcNow)
            .Bind(
                SecretBinding.Create("binding-b", "pole/postgresql", "resource-b", new AllSecretDeployments(), []),
                DateTime.UtcNow);

        public Task Run() => new SecretProjectionReconciler(
            (
                _, _,
                _) => Task.FromResult<Secret?>(Secret),
            (
                    _, _,
                    version, _) =>
                Task.FromResult<SecretVersion?>(
                    new SecretVersion("version", "tenant", "secret", version, null!, DateTime.UtcNow)),
            (
                _, _,
                _) => Task.FromResult<IReadOnlyList<SecretProjection>>(Rows.Values.ToArray()),
            (request, _) => Task.FromResult<IReadOnlyList<PlacementSummaryDto>>(
            [
                new PlacementSummaryDto(
                    "deployment-" + request.ResourceId[^1],
                    "scope",
                    "cell",
                    "target-" + request.ResourceId[^1],
                    1,
                    0,
                    ResourceDeploymentStatus.Pending,
                    null,
                    null!,
                    [],
                    ResourceRevision: 1,
                    FixedReplicas: 1,
                    Ingress: null,
                    DeploymentRevision: 1,
                    DesiredOperation: ResourceDeploymentDesiredOperation.Apply,
                    Isolation: new ResourceDeploymentIsolationDto(
                        "Unknown",
                        "EnforcementRequired",
                        "EnforcementNotEstablished",
                        null)),
            ]),
            (request, _) => Task.FromResult<ResourceDeploymentRuntimePlacementDto?>(
                Removed.Contains(request.DeploymentId)
                    ? null
                    : new ResourceDeploymentRuntimePlacementDto(
                        request.DeploymentId,
                        request.ResourceId,
                        "cell",
                        "target-" + request.ResourceId[^1],
                        "namespace-" + request.ResourceId[^1],
                        1,
                        1,
                        null!,
                        ResourceDeploymentStatus.Pending,
                        ResourceDeploymentDesiredOperation.Apply,
                        ForeignScope.Contains(request.DeploymentId) ? "foreign" : "scope")),
            (
                desired, claim,
                now, _) => Task.FromResult<SecretProjection?>(desired.Claim(claim, now)),
            (row, _) =>
            {
                Rows[row.DeploymentId] = row;
                return Task.CompletedTask;
            },
            Encryption,
            Target,
            new SecretBindingConsumerCatalog([]),
            TimeProvider.System).ReconcileAsync("tenant", "secret", default);
    }

    private sealed class Target : ISecretProjectionTarget
    {
        public readonly Dictionary<string, V1Secret> Manifests = [];
        public readonly HashSet<string> Unavailable = [];
        public Action<SecretProjection>? AfterApply;

        public async Task<SecretProjectionTargetOutcome> ApplyAsync(
            SecretProjection projection,
            Func<CancellationToken, Task<SecretProjectionManifestOutcome>> createManifest,
            CancellationToken ct)
        {
            if (Unavailable.Contains(projection.DeploymentTargetId))
                return new SecretProjectionTargetUnavailable();
            var prepared = await createManifest(ct);
            if (prepared.Value is SecretProjectionTargetFailure error)
                return error;
            var manifest = (V1Secret)prepared.Value;
            Manifests[projection.DeploymentId] = manifest;
            AfterApply?.Invoke(projection);
            foreach (var bytes in manifest.Data.Values)
                CryptographicOperations.ZeroMemory(bytes);
            return new SecretProjectionTargetSucceeded();
        }

        public Task<SecretProjectionTargetOutcome> DeleteAsync(SecretProjection projection, CancellationToken ct)
        {
            if (Unavailable.Contains(projection.DeploymentTargetId))
                return Task.FromResult<SecretProjectionTargetOutcome>(new SecretProjectionTargetUnavailable());
            Manifests.Remove(projection.DeploymentId);
            return Task.FromResult<SecretProjectionTargetOutcome>(new SecretProjectionTargetSucceeded());
        }
    }

    private sealed class Encryption : IEnvelopeEncryptionService
    {
        public bool Fail;
        public EnvelopeEncryptionKeyReference ActiveKey => throw new NotSupportedException();

        public ValueTask<EnvelopeDecryptionOutcome> DecryptAsync(
            EncryptedValue value,
            EnvelopeEncryptionContext context,
            CancellationToken ct = default) => ValueTask.FromResult(
            Fail
                ? new EnvelopeDecryptionOutcome(new EnvelopeDecryptionFailed("sensitive internal detail"))
                : JsonSerializer.SerializeToUtf8Bytes(new { password = "private-value" }));

        public ValueTask<EnvelopeEncryptionOutcome> EncryptAsync(
            ReadOnlyMemory<byte> value,
            EnvelopeEncryptionContext context,
            CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<EnvelopeEncryptionOutcome> EncryptStringAsync(
            string value,
            EnvelopeEncryptionContext context,
            CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<EnvelopeStringDecryptionOutcome> DecryptStringAsync(
            EncryptedValue value,
            EnvelopeEncryptionContext context,
            CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<EnvelopeRewrapOutcome> RewrapAsync(
            EncryptedValue value,
            EnvelopeEncryptionContext context,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
