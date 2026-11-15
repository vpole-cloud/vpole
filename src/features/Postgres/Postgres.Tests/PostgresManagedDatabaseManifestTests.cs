using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.FSharp.Core;
using MongoDB.Bson;
using Pole.Features.Postgres.Application.ManagedDatabases;
using Pole.Features.Postgres.Domain;
using Xunit;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresManagedDatabaseManifestTests
{
    [Fact]
    public void Managed_role_has_no_elevated_privileges_and_database_owns_only_its_private_C_collation_schema()
    {
        var claim = Claim();
        var role = JsonSerializer.SerializeToNode(
            PostgresManagedDatabaseManifests.role(claim, "projected-private-login", "cluster-uid"))!;
        var spec = role["spec"]!;
        Assert.Equal(claim.Username, spec["name"]!.GetValue<string>());
        Assert.True(spec["login"]!.GetValue<bool>());
        foreach (var privilege in new[]
                     { "superuser", "createdb", "createrole", "replication", "bypassrls", "inherit" })
            Assert.False(spec[privilege]!.GetValue<bool>());
        Assert.Empty(spec["inRoles"]!.AsArray());
        Assert.Equal("projected-private-login", spec["passwordSecret"]!["name"]!.GetValue<string>());
        Assert.Equal("delete", spec["databaseRoleReclaimPolicy"]!.GetValue<string>());
        var database = JsonSerializer.SerializeToNode(PostgresManagedDatabaseManifests.database(claim, "cluster-uid"))!;
        Assert.Equal(claim.Database, database["spec"]!["name"]!.GetValue<string>());
        Assert.Equal(claim.Username, database["spec"]!["owner"]!.GetValue<string>());
        Assert.Equal("template0", database["spec"]!["template"]!.GetValue<string>());
        Assert.Equal("UTF8", database["spec"]!["encoding"]!.GetValue<string>());
        Assert.Equal("C", database["spec"]!["localeCType"]!.GetValue<string>());
        Assert.Equal("C", database["spec"]!["localeCollate"]!.GetValue<string>());
        Assert.Equal("delete", database["spec"]!["databaseReclaimPolicy"]!.GetValue<string>());
        Assert.Equal(
            claim.Username,
            Assert.Single(database["spec"]!["schemas"]!.AsArray())!["owner"]!.GetValue<string>());
    }

    [Fact]
    public void Probe_ingress_targets_actual_postgres_pods_and_permits_only_claim_probe_in_physical_namespace()
    {
        var claim = Claim();
        var policy = PostgresManagedDatabaseProbeManifests.policy(claim);
        var selector = policy.Spec.PodSelector.MatchLabels;
        Assert.Equal(PostgresManagedDatabaseManifests.podLabels(claim), selector);
        Assert.DoesNotContain(PostgresManagedDatabaseManifests.ClaimLabel, selector.Keys);
        var ingress = Assert.Single(policy.Spec.Ingress);
        Assert.Equal("5432", Assert.Single(ingress.Ports).Port.Value);
        var peer = Assert.Single(ingress.FromProperty);
        Assert.Equal(
            claim.Target.KubernetesNamespace,
            peer.NamespaceSelector.MatchLabels["kubernetes.io/metadata.name"]);
        Assert.Equal(claim.TenantId, peer.NamespaceSelector.MatchLabels["pole.io/tenant-id"]);
        Assert.Equal(claim.Target.RuntimeCellId, peer.NamespaceSelector.MatchLabels["pole.io/runtime-cell-id"]);
        Assert.Equal(claim.Id, peer.PodSelector.MatchLabels[PostgresManagedDatabaseProbeManifests.ProbeLabel]);
    }

    [Fact]
    public void Native_readiness_rejects_stale_generation_credential_version_and_foreign_ownership()
    {
        var claim = Claim();
        var desired = PostgresManagedDatabaseManifests.role(claim, "projected-login", "cluster-uid");
        var document = JsonSerializer.SerializeToNode(desired)!.AsObject();
        document["metadata"]!["uid"] = "role-uid";
        document["metadata"]!["resourceVersion"] = "22";
        document["metadata"]!["generation"] = 3;
        document["status"] = new JsonObject
            { ["applied"] = true, ["observedGeneration"] = 3, ["secretResourceVersion"] = "41" };
        var native = new PostgresManagedDatabaseNative();
        var version = FSharpOption<string>.Some("41");
        Assert.True(native.Ready(claim, document, desired, version));
        document["metadata"]!.AsObject().Remove("generation");
        document["status"]!.AsObject().Remove("observedGeneration");
        Assert.False(native.Ready(claim, document, desired, version));
        document["metadata"]!["generation"] = 3;
        document["status"]!["observedGeneration"] = 2;
        Assert.False(native.Ready(claim, document, desired, version));
        document["status"]!["observedGeneration"] = 3;
        document["status"]!["secretResourceVersion"] = "40";
        Assert.False(native.Ready(claim, document, desired, version));
        document["status"]!["secretResourceVersion"] = "41";
        document["metadata"]!["labels"]!["pole.io/tenant-id"] = Id();
        Assert.False(native.Ready(claim, document, desired, version));
    }

    [Fact]
    public void Purge_state_retains_identity_and_cleanup_authority_and_cannot_be_reactivated()
    {
        var claim = Claim();
        Assert.Throws<InvalidOperationException>(() => claim.CompletePurge(DateTime.UtcNow));
        var purging = claim.BeginPurge(DateTime.UtcNow);
        var purged = purging.CompletePurge(DateTime.UtcNow);
        Assert.Equal(claim.Id, purged.Id);
        Assert.Equal(claim.Target, purged.Target);
        Assert.Equal(claim.Database, purged.Database);
        Assert.Equal(claim.Username, purged.Username);
        Assert.Equal(3, purged.Revision);
        Assert.True(purged.IsPurged);
        Assert.False(purged.IsActive);
        Assert.Same(purged, purged.BeginPurge(DateTime.UtcNow));
    }

    private static string Id() => ObjectId.GenerateNewId().ToString();

    private static PostgresManagedDatabase Claim()
    {
        var target = Id();
        return PostgresManagedDatabase.Create(
            Id(),
            Id(),
            new PostgresDatabaseOwner("pole/seaweedfs", Id()),
            new PostgresDatabaseSource(Id(), Id(), target, "filer"),
            new PostgresDatabaseTarget(Id(), target, Id(), "pg-physical-namespace"),
            Id(),
            DateTime.UtcNow);
    }
}
