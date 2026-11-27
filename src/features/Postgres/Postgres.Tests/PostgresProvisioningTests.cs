// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.FSharp.Core;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Xunit;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresProvisioningTests
{
    [Fact]
    public void PreparedPlacementIsDeferredAndPinsExplicitCatalogRevisions()
    {
        var cluster = Prepared();
        var target = ObjectId.GenerateNewId().ToString();
        var compute = new ResourceDeploymentComputeConfiguration(
            ObjectId.GenerateNewId().ToString(),
            3,
            target,
            "database",
            new ResourceDeploymentComputeResourceValues(100, 128),
            new ResourceDeploymentComputeResourceValues(500, 512),
            ComputeArchitecture.Any,
            [],
            [],
            [],
            [],
            [],
            null);
        var storage = new ResourceDeploymentStorageConfiguration(cluster.Spec.StorageProfileId, 8, "database-fast");
        var placement = new PostgresPreparedPlacement(
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            new ResolvedResourceDeploymentTarget(target, 5, null),
            compute,
            storage,
            9);

        var request = PostgresPreparedPlacements.createRequest(cluster, placement);
        Assert.True(request.DeferredActivation);
        Assert.True(request.PinnedCompute);
        Assert.Equal(9, request.StoragePolicyRevision);
        Assert.Equal(cluster.Spec.Instances, request.FixedReplicas);
        Assert.Equal(cluster.Revision, request.ResourceRevision);
        Assert.Same(storage, request.Storage);
        Assert.Same(compute, request.Compute);
        Assert.Throws<ArgumentException>(() => PostgresPreparedPlacements.createRequest(
            cluster.ConfigurePrepared(new PostgresDesiredCapacity(1, 20)),
            placement));
    }

    [Fact]
    public void PreparedFactoryPinsBootstrapIdentityWithoutValuesAndBlocksRuntime()
    {
        var cluster = Prepared();
        var prepared = Assert.IsType<PreparedPostgresProvisioning>(cluster.Provisioning);

        Assert.Equal(1, cluster.Revision);
        Assert.Equal(0, cluster.PlanningFence);
        Assert.True(cluster.IsPrepared);
        Assert.Equal("password", prepared.Bootstrap.SourceKey);
        Assert.Throws<InvalidOperationException>(cluster.EnsureConfigured);
        Assert.Throws<InvalidOperationException>(() => cluster.UpdateDesiredState(2, 30));
        Assert.DoesNotContain(
            typeof(PostgresBootstrapCredential).GetProperties(),
            property => property.Name is "Password" or "Value" or "Values");
    }

    [Fact]
    public void ConfigureTransitionsOnceAndPreservesBootstrapThroughUpdates()
    {
        var prepared = Prepared();
        var configured = prepared.ConfigurePrepared(new PostgresDesiredCapacity(1, 20));
        configured.EnsureConfigured();

        Assert.Equal(2, configured.Revision);
        Assert.False(configured.IsPrepared);
        Assert.Same(configured, configured.ConfigurePrepared(new PostgresDesiredCapacity(1, 20)));
        var changed = configured.UpdateDesiredState(3, 30);
        Assert.Equal(3, changed.Revision);
        Assert.Equal(30, changed.Spec.Storage);
        Assert.Equal(
            Assert.IsType<PreparedPostgresProvisioning>(prepared.Provisioning).Bootstrap,
            Assert.IsType<ConfiguredPostgresProvisioning>(changed.Provisioning).Bootstrap);
    }

    [Fact]
    public void Adoption_records_existing_credential_without_changing_capacity_or_reinitializing()
    {
        var prepared = Prepared();
        var bootstrap = Assert.IsType<PreparedPostgresProvisioning>(prepared.Provisioning).Bootstrap;
        var direct = PostgresCluster.Create(
            prepared.TenantId,
            prepared.Name,
            prepared.GroupId,
            prepared.Spec.Version,
            prepared.Spec.StorageProfileId,
            new CreatePostgresClusterOptions(FSharpOption<string>.Some(prepared.Id), 3, 40));

        var adopted = direct.AdoptConfigured(bootstrap);

        Assert.Equal(direct.Id, adopted.Id);
        Assert.Same(direct.Spec, adopted.Spec);
        Assert.Equal(direct.Revision + 1, adopted.Revision);
        Assert.Equal(direct.PlanningFence, adopted.PlanningFence);
        Assert.False(adopted.IsPrepared);
        Assert.Equal(
            bootstrap.CredentialId,
            Assert.IsType<ConfiguredPostgresProvisioning>(adopted.Provisioning).Bootstrap.CredentialId);
        Assert.Same(adopted, adopted.AdoptConfigured(bootstrap));
        adopted.EnsureConfigured();
        Assert.Same(adopted, adopted.ConfigurePrepared(new PostgresDesiredCapacity(3, 40)));
        Assert.Throws<InvalidOperationException>(() => adopted.ConfigurePrepared(new PostgresDesiredCapacity(4, 40)));
        Assert.Throws<InvalidOperationException>(() => adopted.AdoptConfigured(
            PostgresBootstrapCredential.Create(
                bootstrap.SourceSecretId,
                bootstrap.SourceKey,
                ObjectId.GenerateNewId().ToString())));
        Assert.Equal(
            adopted.ToBsonDocument(),
            BsonSerializer.Deserialize<PostgresCluster>(adopted.ToBsonDocument()).ToBsonDocument());
    }

    [Fact]
    public void Adoption_cannot_skip_prepared_bootstrap_or_revive_deleting_cluster()
    {
        var prepared = Prepared();
        var bootstrap = Assert.IsType<PreparedPostgresProvisioning>(prepared.Provisioning).Bootstrap;
        Assert.Throws<InvalidOperationException>(() => prepared.AdoptConfigured(bootstrap));
        var direct = PostgresCluster.Create(
            prepared.TenantId,
            prepared.Name,
            prepared.GroupId,
            prepared.Spec.Version,
            prepared.Spec.StorageProfileId,
            new CreatePostgresClusterOptions(FSharpOption<string>.Some(prepared.Id), 1, 20));
        Assert.Throws<InvalidOperationException>(() =>
            direct.BeginDeletion(DateTime.UtcNow).AdoptConfigured(bootstrap));
    }

    [Fact]
    public void ConfigureAcceptsLatestCapacityInOneRevisionBeforeActivation()
    {
        var prepared = Prepared();
        var configured = prepared.ConfigurePrepared(new PostgresDesiredCapacity(2, 30));
        Assert.Equal(prepared.Revision + 1, configured.Revision);
        Assert.Equal(2, configured.Spec.Instances);
        Assert.Equal(30, configured.Spec.Storage);
        Assert.Same(configured, configured.ConfigurePrepared(new PostgresDesiredCapacity(2, 30)));
        Assert.Throws<InvalidOperationException>(() =>
            configured.ConfigurePrepared(new PostgresDesiredCapacity(3, 30)));
    }

    [Fact]
    public void DeletionCanRemovePreparedIdentityButCannotActivateIt()
    {
        var deleting = Prepared().BeginDeletion(DateTime.UtcNow);
        Assert.IsType<DeletingPostgresClusterLifecycle>(deleting.Lifecycle);
        Assert.Throws<InvalidOperationException>(() => deleting.ConfigurePrepared(new PostgresDesiredCapacity(1, 20)));
        Assert.Throws<InvalidOperationException>(deleting.EnsureConfigured);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("prepared")]
    [InlineData("configured")]
    public void CurrentProvisioningStatesRoundTripThroughBson(string state)
    {
        var prepared = Prepared();
        var cluster = state switch
        {
            "prepared" => prepared,
            "configured" => prepared.ConfigurePrepared(new PostgresDesiredCapacity(1, 20)),
            "direct" => PostgresCluster.Create(
                prepared.TenantId,
                prepared.Name,
                prepared.GroupId,
                prepared.Spec.Version,
                prepared.Spec.StorageProfileId,
                new CreatePostgresClusterOptions(FSharpOption<string>.Some(prepared.Id), 1, 20)),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        var bson = cluster.ToBsonDocument();
        var restored = BsonSerializer.Deserialize<PostgresCluster>(bson);

        Assert.Equal(cluster.Id, restored.Id);
        Assert.Equal(cluster.Revision, restored.Revision);
        Assert.Equal(0, restored.PlanningFence);
        Assert.Equal(cluster.Provisioning.GetType(), restored.Provisioning.GetType());
        Assert.Equal(bson, restored.ToBsonDocument());
        Assert.False(bson.Contains("IsPrepared"));
    }

    internal static PostgresCluster Prepared() => PostgresCluster.CreatePrepared(
        TestData.TenantId,
        "declarative-postgres",
        ObjectId.GenerateNewId().ToString(),
        17,
        TestData.DefaultStorageProfileId,
        new CreatePostgresClusterOptions(FSharpOption<string>.Some(ObjectId.GenerateNewId().ToString()), 1, 20),
        PostgresBootstrapCredential.Create(
            ObjectId.GenerateNewId().ToString(),
            "password",
            ObjectId.GenerateNewId().ToString()));
}
