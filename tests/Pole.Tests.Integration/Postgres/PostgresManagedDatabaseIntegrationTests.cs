using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Core;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Postgres.Application;
using Pole.Features.Postgres.Application.Clusters;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Lifecycle;
using Pole.Features.Postgres.Application.ManagedDatabases;
using Pole.Features.Postgres.Contracts;
using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.ResourceDeployments;
using Xunit;

namespace Pole.Tests.Integration.Postgres;

[Collection(IntegrationTestCollection.Tenant4)]
public sealed class PostgresManagedDatabaseIntegrationTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    private static string Id() => ObjectId.GenerateNewId().ToString();

    private IMongoCollection<PostgresManagedDatabase> Claims =>
        Services.GetRequiredService<IMongoCollection<PostgresManagedDatabase>>();

    private IMongoCollection<PostgresCluster> Clusters =>
        Services.GetRequiredService<IMongoCollection<PostgresCluster>>();

    [Fact]
    public void Managed_database_provider_contracts_resolve_from_production_composition()
    {
        Assert.NotNull(Services.GetRequiredService<ClaimPostgresManagedDatabase>());
        Assert.NotNull(Services.GetRequiredService<EnsurePostgresManagedDatabase>());
        Assert.NotNull(Services.GetRequiredService<PurgePostgresManagedDatabase>());
    }

    [Fact]
    public async Task Claim_is_cross_group_tenant_scoped_idempotent_and_keeps_private_credentials_at_postgres_owner()
    {
        var host = await SeedHost(PostgresReplicationMode.SynchronousHa);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        await StageOwner(
            host.SourceGroup,
            ResourceTypeId.Create(host.Owner.ResourceType),
            host.Owner.ResourceId,
            1,
            session);
        var request = Request(host);
        var first = Assert.IsType<PostgresManagedDatabaseReference>(
            (await Services.GetRequiredService<ClaimPostgresManagedDatabase>()(request, session, default)).Value);
        await session.CommitTransactionAsync();
        var saved = await Claims.Find(item => item.Id == first.ClaimId).SingleAsync();
        Assert.Equal(host.Cluster.Id, saved.ClusterId);
        Assert.Equal(host.SourceGroup.Id, saved.Source.ResourceGroupId);
        Assert.NotEqual(host.Group.Id, saved.Source.ResourceGroupId);
        Assert.Equal(host.Placement.Id, saved.Target.DeploymentId);
        var credential = await Services.GetRequiredService<IMongoCollection<Secret>>()
            .Find(item => item.Id == saved.CredentialId)
            .SingleAsync();
        Assert.Equal(host.Group.Id, credential.ResourceGroupId);
        Assert.Equal(host.Group.DefaultIsolationScopeId, credential.IsolationScopeId);
        var management = Assert.IsType<ProviderManagedSecret>(credential.Management);
        Assert.Equal(host.Cluster.Id, management.ResourceId);
        Assert.Equal($"database:{saved.Id}", management.Purpose);
        Assert.Equal(ManagedSecretSharing.OwnerOnly, management.Sharing);
        Assert.False(credential.AllowsSharing);
        Assert.Equal(1, (await Clusters.Find(item => item.Id == host.Cluster.Id).SingleAsync()).PlanningFence);
        Assert.IsType<SecretReferenceRejected>(
            (await Services.GetRequiredService<PersistSecretBinding>()(
                host.Group.TenantId,
                credential.Id,
                SecretBinding.Create(
                    Id(),
                    host.Owner.ResourceType,
                    host.Owner.ResourceId,
                    new AllSecretDeployments(),
                    []),
                default)).Value);
        session.StartTransaction();
        var repeated = Assert.IsType<PostgresManagedDatabaseReference>(
            (await Services.GetRequiredService<ClaimPostgresManagedDatabase>()(request, session, default)).Value);
        Assert.Equal(first, repeated);
        await session.CommitTransactionAsync();
        Assert.Equal(1, await Claims.CountDocumentsAsync(item => item.ClusterId == host.Cluster.Id));
        Assert.Equal(1, (await Clusters.Find(item => item.Id == host.Cluster.Id).SingleAsync()).PlanningFence);
        var unavailable = await Services.GetRequiredService<EnsurePostgresManagedDatabase>()(
            new EnsurePostgresManagedDatabaseRequest(Id(), host.Owner),
            default);
        Assert.IsType<PostgresManagedDatabaseUnavailable>(unavailable.Value);
    }

    [Fact]
    public async Task Aborted_consumer_create_rolls_back_claim_private_secret_registration_deployment_and_outbox()
    {
        var host = await SeedHost(PostgresReplicationMode.SynchronousHa);
        var before = await DbContext.CountOutboxMessagesAsync(host.Group.TenantId, "pole.secrets/v1/secret-changed");
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var source = await StageOwner(
            host.SourceGroup,
            ResourceTypeId.Create(host.Owner.ResourceType),
            host.Owner.ResourceId,
            1,
            session);
        var reference = Assert.IsType<PostgresManagedDatabaseReference>(
            (await Services.GetRequiredService<ClaimPostgresManagedDatabase>()(Request(host), session, default)).Value);
        var staged = await Claims.Find(session, item => item.Id == reference.ClaimId).SingleAsync();
        await session.AbortTransactionAsync();
        Assert.Equal(0, await Claims.CountDocumentsAsync(item => item.Id == reference.ClaimId));
        Assert.Equal(
            0,
            await Services.GetRequiredService<IMongoCollection<Secret>>()
                .CountDocumentsAsync(item => item.Id == staged.CredentialId));
        Assert.Equal(
            0,
            await Services.GetRequiredService<IMongoCollection<SecretVersion>>()
                .CountDocumentsAsync(item => item.SecretId == staged.CredentialId));
        Assert.Equal(0, await DbContext.ResourceDeployments.CountDocumentsAsync(item => item.Id == source.Id));
        Assert.Null(
            await Services.GetRequiredService<LoadRegisteredResource>()(
                new LoadRegisteredResourceRequest(
                    host.Group.TenantId,
                    ResourceTypeId.Create(host.Owner.ResourceType),
                    host.Owner.ResourceId),
                default));
        Assert.Equal(0, (await Clusters.Find(item => item.Id == host.Cluster.Id).SingleAsync()).PlanningFence);
        Assert.Equal(
            before,
            await DbContext.CountOutboxMessagesAsync(host.Group.TenantId, "pole.secrets/v1/secret-changed"));
    }

    [Fact]
    public async Task Three_asynchronous_replicas_are_ineligible_and_wrong_tenant_cannot_claim()
    {
        var host = await SeedHost(PostgresReplicationMode.Asynchronous);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        await StageOwner(
            host.SourceGroup,
            ResourceTypeId.Create(host.Owner.ResourceType),
            host.Owner.ResourceId,
            1,
            session);
        var claim = Services.GetRequiredService<ClaimPostgresManagedDatabase>();
        var rejected
            = Assert.IsType<PostgresManagedDatabaseIneligible>((await claim(Request(host), session, default)).Value);
        Assert.Equal(PostgresManagedDatabaseIneligibility.SynchronousHaRequired, rejected.Reason);
        var wrongTenant = Request(host) with { TenantId = Id() };
        Assert.IsType<PostgresManagedDatabaseUnavailable>((await claim(wrongTenant, session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(0, await Claims.CountDocumentsAsync(item => item.ClusterId == host.Cluster.Id));
    }

    [Fact]
    public async Task Claim_rejects_a_consumer_request_for_another_target()
    {
        var host = await SeedHost(PostgresReplicationMode.SynchronousHa);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        await StageOwner(
            host.SourceGroup,
            ResourceTypeId.Create(host.Owner.ResourceType),
            host.Owner.ResourceId,
            1,
            session);
        var request = Request(host);
        request = request with { Placement = request.Placement with { DeploymentTargetId = Id() } };
        var result = await Services.GetRequiredService<ClaimPostgresManagedDatabase>()(request, session, default);
        Assert.Equal(
            PostgresManagedDatabaseIneligibility.DeploymentTargetMismatch,
            Assert.IsType<PostgresManagedDatabaseIneligible>(result.Value).Reason);
        await session.AbortTransactionAsync();
        Assert.Equal(0, await Claims.CountDocumentsAsync(item => item.ClusterId == host.Cluster.Id));
    }

    [Fact]
    public async Task Live_claim_blocks_shared_update_and_deletion_boundaries_until_private_credential_purge()
    {
        var host = await SeedHost(PostgresReplicationMode.SynchronousHa);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        await StageOwner(
            host.SourceGroup,
            ResourceTypeId.Create(host.Owner.ResourceType),
            host.Owner.ResourceId,
            1,
            session);
        var reference = Assert.IsType<PostgresManagedDatabaseReference>(
            (await Services.GetRequiredService<ClaimPostgresManagedDatabase>()(Request(host), session, default)).Value);
        await session.CommitTransactionAsync();
        var current = await Clusters.Find(item => item.Id == host.Cluster.Id).SingleAsync();
        session.StartTransaction();
        var unsafeUpdate = await Services.GetRequiredService<PersistUpdatedPostgresCluster>()(
            new PersistUpdatedPostgresClusterRequest(
                current,
                current.UpdateDesiredState(3, 10, PostgresReplicationMode.Asynchronous),
                null),
            session,
            default);
        var updateError
            = Assert.IsType<PostgresOutcome<Unit, PostgresClusterUpdateConflict>.PostgresError>(unsafeUpdate);
        Assert.Equal(PostgresMutationConflictReason.ManagedDatabaseDependency, updateError.Item.Reason);
        await session.AbortTransactionAsync();
        session.StartTransaction();
        var deleting = await Services.GetRequiredService<BeginPostgresClusterDeletion>()(
            new BeginPostgresClusterDeletionRequest(current, current.BeginDeletion(DateTime.UtcNow), null, null),
            session,
            default);
        var deletionError
            = Assert.IsType<PostgresOutcome<Unit, PostgresClusterDeletionConflict>.PostgresError>(deleting);
        Assert.Equal(PostgresMutationConflictReason.ManagedDatabaseDependency, deletionError.Item.Reason);
        await session.AbortTransactionAsync();
        var store = Services.GetRequiredService<PostgresManagedDatabaseStateStore>();
        var purging = (await store.BeginPurgeAsync(host.Group.TenantId, host.Owner, default)).Value;
        Assert.IsType<PurgingPostgresManagedDatabase>(purging.State);
        Assert.True(await store.CompletePurgeAsync(purging, default));
        var tombstone = await Claims.Find(item => item.Id == reference.ClaimId).SingleAsync();
        Assert.IsType<PurgedPostgresManagedDatabase>(tombstone.State);
        Assert.Equal(
            0,
            await Services.GetRequiredService<IMongoCollection<Secret>>()
                .CountDocumentsAsync(item => item.Id == purging.CredentialId));
        Assert.Equal(
            0,
            await Services.GetRequiredService<IMongoCollection<SecretVersion>>()
                .CountDocumentsAsync(item => item.SecretId == purging.CredentialId));
        current = await Clusters.Find(item => item.Id == host.Cluster.Id).SingleAsync();
        session.StartTransaction();
        var allowed = await Services.GetRequiredService<BeginPostgresClusterDeletion>()(
            new BeginPostgresClusterDeletionRequest(current, current.BeginDeletion(DateTime.UtcNow), null, null),
            session,
            default);
        Assert.IsType<PostgresOutcome<Unit, PostgresClusterDeletionConflict>.PostgresSuccess>(allowed);
        await session.AbortTransactionAsync();
        session.StartTransaction();
        Assert.IsType<PostgresManagedDatabaseChanged>(
            (await Services.GetRequiredService<ClaimPostgresManagedDatabase>()(Request(host), session, default)).Value);
        await session.AbortTransactionAsync();
    }

    private async Task<Host> SeedHost(PostgresReplicationMode mode)
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("pg-host"));
        var source = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("metadata-consumer"));
        var cluster = PostgresCluster.Create(
            group.TenantId,
            "metadata-host",
            group.Id,
            17,
            TestData.DefaultStorageProfileId,
            new CreatePostgresClusterOptions(FSharpOption<string>.Some(Id()), 3, 10, mode));
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        await Clusters.InsertOneAsync(session, cluster);
        var placement = await StageOwner(group, PostgresResourceDeployment.ResourceType, cluster.Id, 3, session);
        await session.CommitTransactionAsync();
        return new Host(group, source, cluster, new PostgresManagedDatabaseOwner("pole/web-service", Id()), placement);
    }

    private static ClaimPostgresManagedDatabaseRequest Request(Host host) => new(
        host.Group.TenantId,
        host.Cluster.Id,
        host.Owner,
        new PostgresManagedDatabasePlacement(
            host.SourceGroup.Id,
            host.SourceGroup.DefaultIsolationScopeId,
            TestData.DefaultDeploymentTargetId),
        "filer");

    private async Task<ResourceDeployment> StageOwner(
        ResourceGroup group,
        ResourceTypeId type,
        string id,
        int replicas,
        IClientSessionHandle session)
    {
        Assert.IsType<RegisteredResource>(
            (await Services.GetRequiredService<IResourceRegistrar>()
                .RegisterAsync(
                    new RegisterResourceRequest(
                        group.TenantId,
                        group.Id,
                        new ResourceRegistrationDescriptor(type, id, "managed-owner")),
                    session)).Value);
        var now = DateTime.UtcNow;
        var resolved = ResolvedResourcePlacement.Create(
            Id(),
            TestData.DefaultDeploymentTargetId,
            $"managed-{group.Id}",
            1,
            1,
            ResolvedIngressConfiguration.Create("traefik", "letsencrypt-prod"),
            null,
            ResourceDeploymentTestData.Compute(),
            now);
        var deployment = ResourceDeploymentFactory.Create(
            group.TenantId,
            type,
            id,
            group.Id,
            group.DefaultIsolationScopeId,
            resolved,
            ResourcePlacementCapability.SingleTarget,
            true,
            ResourceDeploymentFactory.DefaultOptions with { FixedReplicas = replicas });
        await DbContext.ResourceDeployments.InsertOneAsync(session, deployment);
        return deployment;
    }

    private sealed record Host(
        ResourceGroup Group,
        ResourceGroup SourceGroup,
        PostgresCluster Cluster,
        PostgresManagedDatabaseOwner Owner,
        ResourceDeployment Placement
    );
}
