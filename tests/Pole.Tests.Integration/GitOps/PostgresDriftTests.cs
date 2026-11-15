using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Core;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Postgres.Application;
using Pole.Features.Postgres.Application.Clusters;
using Pole.Features.Postgres.Application.Declarations;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.Postgres.Application.Lifecycle;
using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.StorageProfiles.Domain;
using Pole.Mediator;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class PostgresDriftTests(
    MongoReplicaSetFixture fixture
) : DeclarativePipelineTestBase(fixture)
{
    [Fact]
    public async Task PostgresBootstrapSameCommitRepairsCapacityPolicyThenNoOpPreservesBusinessState()
    {
        await SeedPostgresPlanningCatalog();
        var resource = await InitializePostgres();
        var bootstrap = Assert.IsType<ConfiguredPostgresProvisioning>(resource.Provisioning).Bootstrap;
        var credential = await Secrets.Find(item => item.Id == bootstrap.CredentialId).SingleAsync();
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == resource.Id).SingleAsync();
        Assert.IsNotType<PreparedResourceDeploymentState>(placement.State);
        Assert.Equal(1, placement.ResolvedPlacement.Storage!.StorageProfileRevision);
        var scope = await DbContext.IsolationScopes
            .Find(item => item.TenantId == Data.TenantId && item.Name == "private-data")
            .SingleAsync();
        Assert.Equal(scope.Id, placement.IsolationScopeId);
        await DbContext.ResourceDeployments.UpdateOneAsync(
            item => item.Id == placement.Id,
            Builders<ResourceDeployment>.Update.Set(item => item.FixedReplicas, 2).Inc(item => item.Revision, 1));
        var downloads = repository.ReadCommits.Count;
        var repairedRun = await Apply(PostgresSnapshot());
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, repairedRun.Status);
        Assert.Equal(downloads, repository.ReadCommits.Count);
        var repaired = await PostgresResources.Find(item => item.Id == resource.Id).SingleAsync();
        Assert.Equal(
            1,
            (await DbContext.ResourceDeployments.Find(item => item.Id == placement.Id).SingleAsync()).FixedReplicas);
        var operations = (await Services.GetRequiredService<GitRepositoryQueries>()
            .OperationsAsync(Data.TenantId, source, repairedRun.Context!.RunId, 1, 20, default))!.Items;
        var configure = operations.Single(item => item.Kind == "PostgresCluster" &&
                item.Phase == DeclarationPhase.Configure)
            .Plan!;
        Assert.Equal(GitPlannedAction.Update, configure.Action);
        Assert.Contains("configuration.fixedReplicas", configure.ChangedFields);
        Assert.DoesNotContain("initial-private-value", JsonSerializer.Serialize(operations));
        var events = await BusinessEvents();
        var stableRun = await Apply(PostgresSnapshot());
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, stableRun.Status);
        Assert.Equal(
            repaired.Revision,
            (await PostgresResources.Find(item => item.Id == resource.Id).SingleAsync()).Revision);
        Assert.Equal(
            credential.CurrentVersion,
            (await Secrets.Find(item => item.Id == credential.Id).SingleAsync()).CurrentVersion);
        Assert.Equal(events, await BusinessEvents());
        var view = await Services.GetRequiredService<GitRepositoryQueries>()
            .RunAsync(Data.TenantId, source, stableRun.Context!.RunId, default);
        Assert.Equal(RepositoryRunStatus.AlreadyCurrent, view!.Status);
        Assert.Equal(new RepositoryOperationSummary(0, 0, 6, 0, 0, null), view.Operations);
    }

    [Fact]
    public async Task PostgresConfiguredMissingCredentialBlocksWithoutRecopyOrRecreation()
    {
        await SeedPostgresPlanningCatalog();
        var resource = await InitializePostgres();
        var bootstrap = Assert.IsType<ConfiguredPostgresProvisioning>(resource.Provisioning).Bootstrap;
        await Secrets.DeleteOneAsync(item => item.Id == bootstrap.CredentialId);
        var events = await BusinessEvents();
        var run = await Apply(PostgresSnapshot());
        Assert.Equal(RepositoryPipelineStatus.Blocked, run.Status);
        Assert.Equal(0, await Secrets.CountDocumentsAsync(item => item.Id == bootstrap.CredentialId));
        Assert.Equal(
            resource.Revision,
            (await PostgresResources.Find(item => item.Id == resource.Id).SingleAsync()).Revision);
        Assert.Equal(events, await BusinessEvents());
    }

    [Fact]
    public async Task PostgresSourceRotationDoesNotRecopyRotatedOwnerCredential()
    {
        await SeedPostgresPlanningCatalog();
        var resource = await InitializePostgres();
        var bootstrap = Assert.IsType<ConfiguredPostgresProvisioning>(resource.Provisioning).Bootstrap;
        var ownerCredential = await Secrets.Find(item => item.Id == bootstrap.CredentialId).SingleAsync();
        var sourceSecret = await Secrets.Find(item => item.Id == bootstrap.SourceSecretId).SingleAsync();
        var prepared = Assert.IsType<PreparedManagedSecret>(
            (await Services.GetRequiredService<PrepareManagedSecret>()(
                new PrepareManagedSecretRequest(
                    resource.TenantId,
                    bootstrap.CredentialId,
                    new ManagedSecretOwner("pole/postgresql", resource.Id, ManagedSecretPurposes.Credentials),
                    ownerCredential.Name,
                    ownerCredential.CurrentVersion + 1,
                    new Dictionary<string, string> { ["username"] = "postgres", ["password"] = "rotated-owner" },
                    ManagedSecretKubernetesType.BasicAuthentication,
                    ManagedSecretSharingPolicy.Shareable),
                default)).Value);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await Services.GetRequiredService<RotateManagedSecret>()(
                new RotateManagedSecretRequest(prepared, ownerCredential.CurrentVersion),
                session,
                default)).Value);
        await session.CommitTransactionAsync();
        Assert.IsType<Secret>(
            (await Services.GetRequiredService<PersistRotatedSecret>()(
                sourceSecret.TenantId,
                sourceSecret.Id,
                sourceSecret.CurrentVersion,
                new Dictionary<string, string> { ["password"] = "later-source-value" },
                default)).Value);
        var versionDocuments = Fixture.Database.GetCollection<BsonDocument>("SecretVersions");
        var versionFilter = new BsonDocument { ["SecretId"] = bootstrap.CredentialId, ["Version"] = prepared.Version };
        var encryptedVersion = await versionDocuments.Find(versionFilter).SingleAsync();
        var events = await BusinessEvents();
        var result = await Apply(PostgresSnapshot());
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, result.Status);
        var stable = await Secrets.Find(item => item.Id == bootstrap.CredentialId).SingleAsync();
        Assert.Equal(prepared.Version, stable.CurrentVersion);
        Assert.Equal(encryptedVersion, await versionDocuments.Find(versionFilter).SingleAsync());
        Assert.Equal(
            resource.Revision,
            (await PostgresResources.Find(item => item.Id == resource.Id).SingleAsync()).Revision);
        Assert.Equal(events, await BusinessEvents());
    }

    [Fact]
    public async Task PostgresManualConfigurationAndDeletionAreFencedByGitOwnership()
    {
        await SeedPostgresPlanningCatalog();
        var resource = await InitializePostgres();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var update = await Services.GetRequiredService<PersistUpdatedPostgresCluster>()
            .Invoke(
                new PersistUpdatedPostgresClusterRequest(
                    resource,
                    resource.UpdateDesiredState(1, 4, resource.Spec.ReplicationMode),
                    null),
                session,
                default);
        Assert.Equal(
            PostgresMutationConflictReason.GitOwnedConfiguration,
            Assert.IsType<PostgresOutcome<Unit, PostgresClusterUpdateConflict>.PostgresError>(update).Item.Reason);
        var delete = await Services.GetRequiredService<BeginPostgresClusterDeletion>()
            .Invoke(
                new BeginPostgresClusterDeletionRequest(resource, resource.BeginDeletion(DateTime.UtcNow), null, null),
                session,
                default);
        Assert.Equal(
            PostgresMutationConflictReason.GitOwnedConfiguration,
            Assert.IsType<PostgresOutcome<Unit, PostgresClusterDeletionConflict>.PostgresError>(delete).Item.Reason);
        await session.AbortTransactionAsync();
        var handler = ActivatorUtilities.CreateInstance<GetPostgresClusterHandler>(Services);
        var endpoint = (IMediatorOutcomeHandler<GetPostgresClusterByIdRequest, GetPostgresClusterByIdOutcome>)handler;
        var detail = await endpoint.HandleAsync(new GetPostgresClusterByIdRequest(resource.Id, resource.TenantId));
        Assert.NotNull(Assert.IsType<GetPostgresClusterByIdOutcome.PostgresClusterFound>(detail).Item.GitOps);
    }

    [Fact]
    public async Task PostgresMissingCreatedIdentityIsNotRecreatedOrRebound()
    {
        await SeedPostgresPlanningCatalog();
        var original = await InitializePostgres();
        await PostgresResources.DeleteOneAsync(item => item.Id == original.Id);
        var result = await Apply(PostgresSnapshot());
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(
            0,
            await PostgresResources.CountDocumentsAsync(
                Builders<PostgresCluster>.Filter.Eq(item => item.TenantId, Data.TenantId)));
    }

    [Fact]
    public async Task PostgresNoOpReplansWhenPlacementChangesAfterPlanning()
    {
        await SeedPostgresPlanningCatalog();
        var original = await InitializePostgres();
        var intercepted = false;
        var mutations = new BeforeNoOp(
            Services.GetRequiredService<IGitOpsMutations>(),
            async (operation, ct) =>
            {
                if (intercepted || operation.Declaration.Address.Kind != "PostgresCluster" ||
                    operation.Phase != DeclarationPhase.Configure)
                    return;

                intercepted = true;
                await DbContext.ResourceDeployments.UpdateOneAsync(
                    item => item.ResourceId == original.Id,
                    Builders<ResourceDeployment>.Update
                        .Set(item => item.FixedReplicas, 2)
                        .Inc(item => item.Revision, 1),
                    cancellationToken: ct);
            });
        var result = await new RepositoryPipelineDriver(Services, Fixture, Data, repository, customMutations: mutations)
            .SubmitAsync(new GitSourceIdentity(Data.TenantId, source), PostgresSnapshot());
        Assert.True(intercepted);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, result.Status);
        Assert.Equal(
            1,
            (await DbContext.ResourceDeployments.Find(item => item.ResourceId == original.Id).SingleAsync())
            .FixedReplicas);
    }

    [Fact]
    public async Task PostgresPrepareRollsBackRootPlacementRegistrationAndEventsOnPlacementFailure()
    {
        await SeedPostgresPlanningCatalog();
        var original = Services.GetRequiredService<CreateResourceDeployment>();
        var called = false;
        CreateResourceDeployment rejectAfterCreate = async (
            request, session,
            ct) =>
        {
            var result = await original(request, session, ct);
            Assert.IsType<PlacementSummaryDto>(result.Value);
            var prepared = await PostgresResources.Find(session, item => item.Id == request.ResourceId).SingleAsync(ct);
            Assert.True(prepared.IsPrepared);
            called = true;
            return new CreateResourceDeploymentError(new ResourcePlacementUnavailable(request.ResourceGroupId));
        };
        var execution = ActivatorUtilities.CreateInstance<PostgresDeclarationExecution>(Services, rejectAfterCreate);
        var failing = ActivatorUtilities.CreateInstance<DeclarativePostgresProvider>(Services, execution);
        var providers = Services.GetServices<IDeclarativeResourceProvider>();
        var registry
            = new DeclarativeProviderRegistry(
                providers.Select(item => item.Kind == "PostgresCluster" ? failing : item));
        var result = await new RepositoryPipelineDriver(Services, Fixture, Data, repository, customProviders: registry)
            .SubmitAsync(new GitSourceIdentity(Data.TenantId, source), PostgresSnapshot());
        Assert.True(called);
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(
            0,
            await PostgresResources.CountDocumentsAsync(
                Builders<PostgresCluster>.Filter.Eq(item => item.TenantId, Data.TenantId)));
        Assert.Equal(
            0,
            await DbContext.ResourceDeployments.CountDocumentsAsync(item =>
                item.TenantId == Data.TenantId && item.ResourceType == "pole/postgresql"));
        Assert.Equal(
            0,
            await DbContext.ResourceRegistrations.CountDocumentsAsync(item =>
                item.TenantId == Data.TenantId && item.DisplayName == "database"));
        var owned = await Inventory.Find(item => item.TenantId == Data.TenantId && item.Kind == "PostgresCluster")
            .SingleAsync();
        Assert.Equal(GitResourceMaterialization.Allocated, owned.Materialization);
        Assert.Null(owned.Projections.Applied);
    }

    [Fact]
    public async Task PostgresPreparedConfigureAcceptsLatestCapacityOnceAndOperatorSupportsGrowth()
    {
        await SeedPostgresPlanningCatalog();
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(PostgresSnapshot())).Status);
        var prepared = await PostgresResources
            .Find(Builders<PostgresCluster>.Filter.Eq(item => item.TenantId, Data.TenantId))
            .SingleAsync();
        await Supply("initial-private-value");
        var configuredRun = await Apply(PostgresSnapshot(size: 6, instances: 2));
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, configuredRun.Status);
        var configured = await PostgresResources.Find(item => item.Id == prepared.Id).SingleAsync();
        Assert.Equal(prepared.Revision + 1, configured.Revision);
        Assert.Equal(2, configured.Spec.Instances);
        Assert.Equal(6, configured.Spec.Storage);
        var updatedRun = await Apply(PostgresSnapshot(size: 8, instances: 3));
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, updatedRun.Status);
        var updated = await PostgresResources.Find(item => item.Id == prepared.Id).SingleAsync();
        Assert.Equal(configured.Revision + 1, updated.Revision);
        Assert.Equal(3, updated.Spec.Instances);
        Assert.Equal(8, updated.Spec.Storage);
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(PostgresSnapshot(size: 7, instances: 3))).Status);
    }

    [Fact]
    public async Task PostgresRequiresBootstrapGrantBeforeAnyMutation()
    {
        await SeedPostgresPlanningCatalog();
        var files = PostgresSnapshot()
            .Select(item => item with
            {
                Content = item.Content.Replace("usage: Bootstrap", "usage: Runtime", StringComparison.Ordinal),
            })
            .ToArray();
        var result = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.InvalidSnapshot, result.Status);
        Assert.Equal(
            0,
            await PostgresResources.CountDocumentsAsync(
                Builders<PostgresCluster>.Filter.Eq(item => item.TenantId, Data.TenantId)));
        Assert.Equal(
            0,
            await Secrets.CountDocumentsAsync(Builders<Secret>.Filter.Eq(item => item.TenantId, Data.TenantId)));
    }

    [Fact]
    public async Task PostgresConfigureRollsBackCredentialActivationRootAndOutboxOnPlacementFailure()
    {
        await SeedPostgresPlanningCatalog();
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(PostgresSnapshot())).Status);
        var prepared = await PostgresResources
            .Find(Builders<PostgresCluster>.Filter.Eq(item => item.TenantId, Data.TenantId))
            .SingleAsync();
        var bootstrap = Assert.IsType<PreparedPostgresProvisioning>(prepared.Provisioning).Bootstrap;
        await Supply("initial-private-value");
        var original = Services.GetRequiredService<RequestResourceDeploymentSetApply>();
        var called = false;
        RequestResourceDeploymentSetApply rejectAfterActivation = async (
            request, session,
            ct) =>
        {
            var result = await original(request, session, ct);
            Assert.IsType<MutatedResourceDeployments>(result.Value);
            Assert.Equal(
                1,
                await Secrets.CountDocumentsAsync(
                    session,
                    item => item.Id == bootstrap.CredentialId,
                    cancellationToken: ct));
            called = true;
            return new ResourceDeploymentMutationError(new ResourceDeploymentPlacementUnavailable(request.ResourceId));
        };
        var execution = ActivatorUtilities.CreateInstance<PostgresDeclarationExecution>(
            Services,
            rejectAfterActivation);
        var failing = ActivatorUtilities.CreateInstance<DeclarativePostgresProvider>(Services, execution);
        var providers = Services.GetServices<IDeclarativeResourceProvider>();
        var registry
            = new DeclarativeProviderRegistry(
                providers.Select(item => item.Kind == "PostgresCluster" ? failing : item));
        var events = await BusinessEvents();
        var result = await new RepositoryPipelineDriver(Services, Fixture, Data, repository, customProviders: registry)
            .SubmitAsync(new GitSourceIdentity(Data.TenantId, source), PostgresSnapshot());
        Assert.True(called);
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(0, await Secrets.CountDocumentsAsync(item => item.Id == bootstrap.CredentialId));
        Assert.Equal(
            prepared.Revision,
            (await PostgresResources.Find(item => item.Id == prepared.Id).SingleAsync()).Revision);
        Assert.IsType<PreparedResourceDeploymentState>(
            (await DbContext.ResourceDeployments.Find(item => item.ResourceId == prepared.Id).SingleAsync()).State);
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(PostgresSnapshot())).Status);
        Assert.Equal(1, await Secrets.CountDocumentsAsync(item => item.Id == bootstrap.CredentialId));
    }
}

[Collection(IntegrationTestCollection.Global)]
public sealed class PostgresDriftGlobalTests(
    MongoReplicaSetFixture fixture
) : IsolatedDeclarativePipelineTestBase(fixture)
{
    [Theory]
    [InlineData("version")]
    [InlineData("storageRevision")]
    [InlineData("scope")]
    public async Task PostgresImmutableChangesBlockWithoutTouchingCredentials(string field)
    {
        await SeedPostgresPlanningCatalog();
        var original = await InitializePostgres();
        var storage = await DbContext.StorageProfiles
            .Find(item => item.Id == NamedCatalogTestData.PostgresStorageProfileId)
            .SingleAsync();
        var updated = storage with { Revision = 2 };
        await DbContext.StorageProfiles.ReplaceOneAsync(item => item.Id == storage.Id, updated);
        await Fixture.Database
            .GetCollection<StorageProfileRevision>("StorageProfileRevisions")
            .InsertOneAsync(StorageProfileRevisionFactory.Create(updated));
        var events = await BusinessEvents();
        var result = await Apply(
            PostgresSnapshot(
                version: field == "version" ? 17 : 16,
                scope: field == "scope" ? null : "private-data",
                storageRevision: field == "storageRevision" ? 2 : 1));
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(
            original.Revision,
            (await PostgresResources.Find(item => item.Id == original.Id).SingleAsync()).Revision);
        Assert.Equal(events, await BusinessEvents());
    }
}
