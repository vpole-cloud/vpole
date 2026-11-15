using Microsoft.FSharp.Core;
using MongoDB.Bson;
using Pole.Features.Postgres.Domain;
using Xunit;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresDomainTests
{
    [Fact]
    public void Factory_CreatesActiveLogicalClusterWithoutPhysicalPlacement()
    {
        var cluster = Create();

        Assert.Equal(1, cluster.Revision);
        Assert.IsType<ActivePostgresClusterLifecycle>(cluster.Lifecycle);
        Assert.Equal(TestData.DefaultStorageProfileId, cluster.Spec.StorageProfileId);
        Assert.DoesNotContain(
            cluster.Spec.GetType().GetProperties(),
            property => property.Name.Contains("Namespace", StringComparison.Ordinal));
        Assert.DoesNotContain(
            cluster.Spec.GetType().GetProperties(),
            property => property.Name.Contains("StorageClass", StringComparison.Ordinal));
        Assert.DoesNotContain(
            cluster.Spec.GetType().GetProperties(),
            property => property.Name.Equals("Resources", StringComparison.Ordinal));
    }

    [Fact]
    public void BeginDeletion_IsIdempotent_AndFencesUpdates()
    {
        var cluster = Create();
        var deleting = cluster.BeginDeletion(DateTime.UtcNow);

        Assert.Equal(2, deleting.Revision);
        Assert.IsType<DeletingPostgresClusterLifecycle>(deleting.Lifecycle);
        Assert.Same(deleting, deleting.BeginDeletion(DateTime.UtcNow.AddMinutes(1)));
        Assert.Throws<InvalidOperationException>(() => deleting.UpdateDesiredState(
            2,
            20,
            deleting.Spec.ReplicationMode));
    }

    private static PostgresCluster Create() => PostgresCluster.Create(
        TestData.TenantId,
        "postgres-domain",
        ObjectId.GenerateNewId().ToString(),
        17,
        TestData.DefaultStorageProfileId,
        new CreatePostgresClusterOptions(
            FSharpOption<string>.Some(ObjectId.GenerateNewId().ToString()),
            1,
            10,
            PostgresReplicationMode.Asynchronous));
}
