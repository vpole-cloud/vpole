using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Domain;
using Xunit;

namespace Pole.Features.Secrets.Tests;

public sealed class SecretProjectionTests
{
    private static Secret Secret() => SecretFactory.Create(
        "012345678901234567890123",
        "112345678901234567890123",
        "212345678901234567890123",
        "312345678901234567890123",
        "database",
        "credentials",
        ["username", "password"],
        DateTime.UtcNow);

    private static SecretBinding Binding() => SecretBinding.Create(
        "412345678901234567890123",
        "pole/postgresql",
        "512345678901234567890123",
        new AllSecretDeployments(),
        ImmutableDictionary<string, string>.Empty.Add("password", "PGPASSWORD"));

    [Fact]
    public void Shared_secret_has_independent_deterministic_per_deployment_identity()
    {
        var first = SecretProjectionFactory.Create(
            Secret(),
            Binding(),
            "612345678901234567890123",
            "cell",
            "target",
            "namespace");
        var repeated = SecretProjectionFactory.Create(
            Secret(),
            Binding(),
            "612345678901234567890123",
            "cell",
            "target",
            "namespace");
        var other = SecretProjectionFactory.Create(
            Secret(),
            Binding(),
            "712345678901234567890123",
            "cell",
            "other",
            "other-namespace");
        Assert.Equal(first.Id, repeated.Id);
        Assert.Equal(first.KubernetesName, repeated.KubernetesName);
        Assert.NotEqual(first.KubernetesName, other.KubernetesName);
        Assert.True(first.KubernetesName.Length <= 63);
    }

    [Fact]
    public void Manifest_selects_only_bound_keys_and_contains_reference_only_metadata()
    {
        var projection = SecretProjectionFactory.Create(
            Secret(),
            Binding(),
            "612345678901234567890123",
            "cell",
            "target",
            "namespace");
        var manifest = SecretProjectionManifest.Create(
            projection,
            Binding(),
            new Dictionary<string, string>
            {
                ["username"] = "private-user",
                ["password"] = "private-password",
            });
        Assert.Single(manifest.Data);
        Assert.Equal("private-password", Encoding.UTF8.GetString(manifest.Data["PGPASSWORD"]));
        Assert.Equal("namespace", manifest.Metadata.NamespaceProperty);
        Assert.Equal("target", manifest.Metadata.Labels["pole.io/deployment-target-id"]);
        Assert.Equal(projection.BindingId, manifest.Metadata.Labels["pole.io/secret-binding-id"]);
        Assert.Equal("1", manifest.Metadata.Annotations["pole.io/secret-version"]);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(manifest.Metadata));
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(projection));
    }

    [Fact]
    public void Basic_authentication_type_is_reserved_for_owner_projection_not_remapped_shared_consumer()
    {
        var binding = Binding() with { KeyMappings = ImmutableDictionary<string, string>.Empty };
        var source = Secret();
        var secret = SecretFactory.CreateManaged(
            source.Id,
            source.TenantId,
            source.ResourceGroupId,
            source.IsolationScopeId,
            source.Name,
            source.Purpose,
            source.KeyNames,
            binding,
            "credentials",
            KubernetesSecretType.BasicAuthentication,
            ManagedSecretSharing.Shareable,
            source.CreatedAtUtc);
        var owner = SecretProjectionFactory.Create(secret, binding, "owner-deployment", "cell", "target", "namespace");
        var shared = SecretProjectionFactory.Create(
            secret,
            Binding() with { Id = "shared-binding" },
            "shared-deployment",
            "cell",
            "target",
            "namespace");
        var values = new Dictionary<string, string> { ["username"] = "user", ["password"] = "password" };
        Assert.Equal("kubernetes.io/basic-auth", SecretProjectionManifest.Create(owner, binding, values).Type);
        Assert.Equal("Opaque", SecretProjectionManifest.Create(shared, Binding(), values).Type);
    }

    [Fact]
    public void Failed_rotation_preserves_previous_applied_version_until_success()
    {
        var projection = SecretProjectionFactory
            .Create(Secret(), Binding(), "deployment", "cell", "target", "namespace")
            .Applied();
        var failed = (projection with { DesiredVersion = 2 }).Failed("TargetUnavailable");
        Assert.Equal(1, failed.AppliedVersion);
        Assert.Equal(2, failed.DesiredVersion);
        Assert.IsType<FailedSecretProjection>(failed.State);
        Assert.Equal(2, failed.Applied().AppliedVersion);
        Assert.Null(failed.Deleted().AppliedVersion);
    }
}
