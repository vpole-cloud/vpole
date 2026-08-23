using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Application.Migrations;
using Pole.Features.ResourceGroups.Application.Services;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Shared.Models;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceGroups;

public sealed class ResourcePlacementIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private const string DefaultTargetId = "507f1f77bcf86cd799439071";
    private const string SecondTargetId = "507f1f77bcf86cd799439072";

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new ResourceGroupPlacementMigration().Up(Fixture.Database);
        await new RuntimeCellLookupMigration().Up(Fixture.Database);
    }

    [Fact]
    public async Task ConcurrentResolve_CreatesOneCellReferenceAndInitialEvent()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var lookup = new PlacementTargetLookup();
        var service = CreateService(lookup);

        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => ResolveAsync(service, resourceGroup.Id, null, null)));

        var placements = results
            .Select(result => result.AssertSuccess())
            .ToList();
        Assert.Single(placements.Select(placement => placement.RuntimeCellId).Distinct());
        Assert.Equal(1L, await DbContext.RuntimeCells.CountDocumentsAsync(
            cell => cell.ResourceGroupId == resourceGroup.Id));
        Assert.Equal(1L, await DbContext.DeploymentTargetReferences.CountDocumentsAsync(
            reference =>
                reference.OwnerType == ResourcePlacementService.ReferenceOwnerType &&
                reference.OwnerId == placements[0].RuntimeCellId));
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "runtime-cell-reconciliation-requested"));
        var requestMessage = await DbContext.OutboxMessages
            .Find(message =>
                message.EventType == "runtime-cell-reconciliation-requested")
            .SingleAsync();
        Assert.Equal(
            RuntimeCellPendingKey.Reconciliation(
                placements[0].RuntimeCellId,
                1),
            requestMessage.PendingKey);
        Assert.IsType<PendingOutboxMessageState>(requestMessage.State);
    }

    [Fact]
    public async Task ExistingCell_RemainsUsable_WhenTargetRejectsNewWorkloads()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var lookup = new PlacementTargetLookup();
        var service = CreateService(lookup);
        var created = await ResolveAsync(service, resourceGroup.Id, null, DefaultTargetId);
        var first = created.AssertSuccess();

        lookup.RejectNewWorkloads = true;
        var existing = await ResolveAsync(service, resourceGroup.Id, null, DefaultTargetId);
        var rejectedNewTarget = await ResolveAsync(service, resourceGroup.Id, null, SecondTargetId);

        var same = existing.AssertSuccess();
        Assert.Equal(first.RuntimeCellId, same.RuntimeCellId);
        var failure = rejectedNewTarget.AssertFailure();
        Assert.IsType<ConflictFailure>(failure);
        Assert.Equal(1L, await DbContext.RuntimeCells.CountDocumentsAsync(
            cell => cell.ResourceGroupId == resourceGroup.Id));
    }

    [Fact]
    public async Task SameScope_UsesSameNamespaceName_InDifferentTargets()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var service = CreateService(new PlacementTargetLookup());

        var firstResult = await ResolveAsync(
            service,
            resourceGroup.Id,
            resourceGroup.DefaultIsolationScopeId,
            DefaultTargetId);
        var secondResult = await ResolveAsync(
            service,
            resourceGroup.Id,
            resourceGroup.DefaultIsolationScopeId,
            SecondTargetId);

        var first = firstResult.AssertSuccess();
        var second = secondResult.AssertSuccess();
        Assert.NotEqual(first.RuntimeCellId, second.RuntimeCellId);
        Assert.NotEqual(first.DeploymentTargetId, second.DeploymentTargetId);
        Assert.Equal(first.KubernetesNamespace, second.KubernetesNamespace);
    }

    [Fact]
    public async Task BatchResolve_CreatesCellsOnce_AndPreservesRequestedTargetOrder()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var service = CreateService(new PlacementTargetLookup());

        var result = await DbContext.RunInTransactionAsync<IReadOnlyList<ResourcePlacement>>(
            (session, cancellationToken) => service.ResolveOrCreateManyAsync(
                resourceGroup.TenantId,
                resourceGroup.Id,
                resourceGroup.DefaultIsolationScopeId,
                [SecondTargetId, DefaultTargetId],
                session,
                cancellationToken));

        var placements = result.AssertSuccess();
        Assert.Equal(
            [SecondTargetId, DefaultTargetId],
            placements.Select(placement => placement.DeploymentTargetId));
        Assert.Equal(2L, await DbContext.RuntimeCells.CountDocumentsAsync(
            cell => cell.ResourceGroupId == resourceGroup.Id));
        Assert.Equal(2L, await DbContext.CountOutboxMessagesAsync(
            "runtime-cell-reconciliation-requested"));
    }

    [Fact]
    public async Task Find_many_returns_requested_tenant_scoped_placement_snapshots()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var service = CreateService(new PlacementTargetLookup());
        var first = (await ResolveAsync(service, resourceGroup.Id, null, DefaultTargetId)).AssertSuccess();
        var second = (await ResolveAsync(service, resourceGroup.Id, null, SecondTargetId)).AssertSuccess();
        var lookup = new ResourcePlacementLookup(DbContext);

        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var placements = await lookup.FindManyAsync(
            resourceGroup.TenantId,
            [first.RuntimeCellId, second.RuntimeCellId, ObjectId.GenerateNewId().ToString()],
            session);
        await session.CommitTransactionAsync();

        Assert.Equal(
            [first.RuntimeCellId, second.RuntimeCellId],
            placements
                .Select(placement => placement.RuntimeCellId)
                .OrderBy(id => id, StringComparer.Ordinal));
        Assert.All(placements, placement =>
            Assert.Equal(resourceGroup.Id, placement.ResourceGroupId));
    }

    [Fact]
    public async Task ConcurrentPlacementAndDeletion_DoNotLeaveActivePlacementUnderDeletedAggregate()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var service = CreateService(new PlacementTargetLookup());

        await Task.WhenAll(
            ResolveAsync(service, resourceGroup.Id, null, DefaultTargetId),
            Mediator.Send(new DeleteResourceGroupRequest(
                resourceGroup.Id,
                resourceGroup.TenantId)));

        var savedGroup = await DbContext.ResourceGroups
            .Find(group => group.Id == resourceGroup.Id)
            .FirstOrDefaultAsync();
        var cells = await DbContext.RuntimeCells
            .Find(cell => cell.ResourceGroupId == resourceGroup.Id)
            .ToListAsync();
        if (savedGroup is null)
        {
            Assert.Empty(cells);
            return;
        }

        Assert.IsType<DeletingResourceGroupLifecycleState>(savedGroup.Lifecycle);
        Assert.All(cells, cell =>
            Assert.IsType<DeletingRuntimeCellState>(cell.State));
    }

    [Fact]
    public async Task ConcurrentExistingCellReuseAndDeletion_AreParentFenced()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var service = CreateService(new PlacementTargetLookup());
        var initial = await ResolveAsync(
            service,
            resourceGroup.Id,
            null,
            DefaultTargetId);
        _ = initial.AssertSuccess();

        await Task.WhenAll(
            ResolveAsync(service, resourceGroup.Id, null, DefaultTargetId),
            Mediator.Send(new DeleteResourceGroupRequest(
                resourceGroup.Id,
                resourceGroup.TenantId)));

        var savedGroup = await DbContext.ResourceGroups
            .Find(group => group.Id == resourceGroup.Id)
            .FirstOrDefaultAsync();
        var cells = await DbContext.RuntimeCells
            .Find(cell => cell.ResourceGroupId == resourceGroup.Id)
            .ToListAsync();
        if (savedGroup is null)
        {
            Assert.Empty(cells);
            return;
        }

        Assert.IsType<DeletingResourceGroupLifecycleState>(savedGroup.Lifecycle);
        Assert.All(cells, cell =>
            Assert.IsType<DeletingRuntimeCellState>(cell.State));
    }

    private ResourcePlacementService CreateService(PlacementTargetLookup lookup) =>
        new(
            DbContext,
            lookup.LookupAsync,
            lookup.LookupDefaultAsync,
            lookup.AdmitForWorkloadAsync,
            lookup.AdmitDefaultForNewWorkloadAsync,
            new MongoReferenceAdmission(DbContext).ApplyChangesOutcomeAsync,
            new RuntimeCellNamespaceNameGenerator(
                new KubernetesOptions { NamespaceNamePrefix = "pole-test" }),
            TimeProvider.System);

    private Task<Result<ResourcePlacement>> ResolveAsync(
        IResourcePlacementService service,
        string resourceGroupId,
        string? isolationScopeId,
        string? deploymentTargetId) =>
        DbContext.RunInTransactionAsync<ResourcePlacement>(
            (session, cancellationToken) => service.ResolveOrCreateAsync(
                TestData.TenantId,
                resourceGroupId,
                isolationScopeId,
                deploymentTargetId,
                session,
                cancellationToken));

    private sealed class PlacementTargetLookup
    {
        public bool RejectNewWorkloads { get; set; }

        public Task<Result<
            DeploymentTargetLookupDto,
            DeploymentTargetLookupError>> LookupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<DeploymentTargetLookupDto, DeploymentTargetLookupError>>(
                new Ok<DeploymentTargetLookupDto>(CreateTarget(deploymentTargetId)));

        public Task<Result<
            DeploymentTargetLookupDto,
            DefaultDeploymentTargetLookupError>> LookupDefaultAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<
                DeploymentTargetLookupDto,
                DefaultDeploymentTargetLookupError>>(
                new Ok<DeploymentTargetLookupDto>(CreateTarget(DefaultTargetId)));

        public Task<Result<
            DeploymentTargetLookupDto,
            DeploymentTargetWorkloadAdmissionError>> AdmitForWorkloadAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<
                DeploymentTargetLookupDto,
                DeploymentTargetWorkloadAdmissionError>>(
                RejectNewWorkloads
                    ? new Err<DeploymentTargetWorkloadAdmissionError>(
                        new DeploymentTargetWorkloadAdmissionError(
                            new DeploymentTargetNewWorkloadsDisabled(deploymentTargetId)))
                    : new Ok<DeploymentTargetLookupDto>(CreateTarget(deploymentTargetId)));

        public async Task<Result<
            DeploymentTargetLookupDto,
            DefaultDeploymentTargetWorkloadAdmissionError>> AdmitDefaultForNewWorkloadAsync(
            CancellationToken cancellationToken = default)
        {
            var admission = await AdmitForWorkloadAsync(DefaultTargetId, cancellationToken);
            return admission switch
            {
                Ok<DeploymentTargetLookupDto> target => new Ok<DeploymentTargetLookupDto>(target.Value),
                Err<DeploymentTargetWorkloadAdmissionError> error => error.Value switch
                {
                    DeploymentTargetDisabled disabled =>
                        new Err<DefaultDeploymentTargetWorkloadAdmissionError>(disabled),
                    DeploymentTargetDeletionRequested deleting =>
                        new Err<DefaultDeploymentTargetWorkloadAdmissionError>(deleting),
                    DeploymentTargetNewWorkloadsDisabled unavailable =>
                        new Err<DefaultDeploymentTargetWorkloadAdmissionError>(unavailable),
                    DeploymentTargetNotFound notFound => throw new InvalidOperationException(
                        $"Configured default deployment target '{notFound.DeploymentTargetId}' does not exist."),
                    _ => throw new InvalidOperationException(
                        "Test deployment-target admission returned an unsupported error case.")
                },
                _ => throw new InvalidOperationException(
                    "Test deployment-target admission returned an unsupported result case.")
            };
        }

        private static DeploymentTargetLookupDto CreateTarget(string id) =>
            new(
                id,
                $"Target {id[^2..]}",
                $"test:kubernetes:{id}",
                "InCluster",
                ConnectionVersion: 1,
                new DeploymentTargetWorkloadConfigurationLookupDto(
                    Revision: 1,
                    new DeploymentTargetIngressConfigurationLookupDto(
                        "traefik",
                        "letsencrypt-prod")),
                Revision: 1,
                Enabled: true,
                AcceptsWorkloads: true,
                DeletionRequested: false,
                HealthStatus: "Healthy");
    }

    private sealed class MongoReferenceAdmission(
        Pole.Server.Database.MongoDbContext dbContext)
    {
        public async Task<Result<Unit, DeploymentTargetReferenceChangeRejected>>
            ApplyChangesOutcomeAsync(
                DeploymentTargetReferenceOwner owner,
                IReadOnlyCollection<string> addedDeploymentTargetIds,
                IReadOnlyCollection<string> removedDeploymentTargetIds,
                IClientSessionHandle session,
                CancellationToken cancellationToken = default)
        {
            foreach (var deploymentTargetId in addedDeploymentTargetIds)
            {
                await dbContext.DeploymentTargetReferences.UpdateOneAsync(
                    session,
                    DeploymentTargetReferenceFilters.ByOwner(deploymentTargetId, owner),
                    Builders<DeploymentTargetReference>.Update
                        .SetOnInsert(reference => reference.Id, ObjectId.GenerateNewId().ToString())
                        .SetOnInsert(reference => reference.DeploymentTargetId, deploymentTargetId)
                        .SetOnInsert(reference => reference.OwnerType, owner.Type)
                        .SetOnInsert(reference => reference.OwnerId, owner.Id)
                        .SetOnInsert(reference => reference.CreatedAtUtc, DateTime.UtcNow),
                    new UpdateOptions { IsUpsert = true },
                    cancellationToken);
            }
            foreach (var deploymentTargetId in removedDeploymentTargetIds)
            {
                await dbContext.DeploymentTargetReferences.DeleteOneAsync(
                    session,
                    DeploymentTargetReferenceFilters.ByOwner(deploymentTargetId, owner),
                    cancellationToken: cancellationToken);
            }

            return new Ok<Unit>(Unit.Value);
        }
    }
}
