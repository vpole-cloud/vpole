using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Application.Migrations;
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
    private const string DefaultTargetId = TestData.DefaultDeploymentTargetId;
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
        var resolve = Services.GetRequiredService<ResolveResourceGroupPlacement>();
        var targets = Services.GetRequiredService<TestDeploymentTargetLookup>();

        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => ResolveAsync(resolve, resourceGroup.Id, null, null)));

        var placements = results.Select(AssertPlacement).ToList();
        Assert.Single(placements.Select(placement => placement.RuntimeCellId).Distinct());
        Assert.Equal(1L, await DbContext.RuntimeCells.CountDocumentsAsync(
            cell => cell.ResourceGroupId == resourceGroup.Id));
        var referenceChange = Assert.Single(targets.ReferenceChanges);
        Assert.Equal(ResourcePlacementReferenceOwnerTypes.RuntimeCell, referenceChange.Owner.Type);
        Assert.Equal(placements[0].RuntimeCellId, referenceChange.Owner.Id);
        Assert.Equal([DefaultTargetId], referenceChange.AddedDeploymentTargetIds);
        Assert.Empty(referenceChange.RemovedDeploymentTargetIds);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "pole.resource-groups/v1/runtime-cell-reconciliation-requested"));
        var requestMessage = await DbContext.OutboxMessages
            .Find(message => message.EventType == "pole.resource-groups/v1/runtime-cell-reconciliation-requested")
            .SingleAsync();
        Assert.Equal(
            RuntimeCellPendingKey.Reconciliation(placements[0].RuntimeCellId, 1),
            requestMessage.PendingKey);
        Assert.IsType<PendingOutboxMessageState>(requestMessage.State);
    }

    [Fact]
    public async Task ExistingCell_RemainsUsable_WhenTargetRejectsNewWorkloads()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var targets = ConfigureTargets();
        var resolve = Services.GetRequiredService<ResolveResourceGroupPlacement>();
        var first = AssertPlacement(await ResolveAsync(
            resolve,
            resourceGroup.Id,
            null,
            DefaultTargetId));

        targets.RejectNewWorkloads = true;
        var existing = await ResolveAsync(resolve, resourceGroup.Id, null, DefaultTargetId);
        var rejectedNewTarget = await ResolveAsync(resolve, resourceGroup.Id, null, SecondTargetId);

        Assert.Equal(first.RuntimeCellId, AssertPlacement(existing).RuntimeCellId);
        _ = AssertDeploymentTargetRejected(rejectedNewTarget);
        Assert.Equal(1L, await DbContext.RuntimeCells.CountDocumentsAsync(
            cell => cell.ResourceGroupId == resourceGroup.Id));
    }

    [Fact]
    public async Task SameScope_UsesSameNamespaceName_InDifferentTargets()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        ConfigureTargets();
        var resolve = Services.GetRequiredService<ResolveResourceGroupPlacement>();

        var first = AssertPlacement(await ResolveAsync(
            resolve,
            resourceGroup.Id,
            resourceGroup.DefaultIsolationScopeId,
            DefaultTargetId));
        var second = AssertPlacement(await ResolveAsync(
            resolve,
            resourceGroup.Id,
            resourceGroup.DefaultIsolationScopeId,
            SecondTargetId));

        Assert.NotEqual(first.RuntimeCellId, second.RuntimeCellId);
        Assert.NotEqual(first.DeploymentTargetId, second.DeploymentTargetId);
        Assert.Equal(first.KubernetesNamespace, second.KubernetesNamespace);
    }

    [Fact]
    public async Task BatchResolve_CreatesCellsOnce_AndPreservesRequestedTargetOrder()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        ConfigureTargets();
        var resolve = Services.GetRequiredService<ResolveResourceGroupPlacements>();

        var placements = AssertPlacements(await ResolveManyAsync(
            resolve,
            resourceGroup.Id,
            resourceGroup.DefaultIsolationScopeId,
            [SecondTargetId, DefaultTargetId]));

        Assert.Equal(
            [SecondTargetId, DefaultTargetId],
            placements.Select(placement => placement.DeploymentTargetId));
        Assert.Equal(2L, await DbContext.RuntimeCells.CountDocumentsAsync(
            cell => cell.ResourceGroupId == resourceGroup.Id));
        Assert.Equal(2L, await DbContext.CountOutboxMessagesAsync(
            "pole.resource-groups/v1/runtime-cell-reconciliation-requested"));
    }

    [Fact]
    public async Task LoadMany_ReturnsRequestedTenantScopedPlacementSnapshots()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        ConfigureTargets();
        var resolve = Services.GetRequiredService<ResolveResourceGroupPlacement>();
        var first = AssertPlacement(await ResolveAsync(resolve, resourceGroup.Id, null, DefaultTargetId));
        var second = AssertPlacement(await ResolveAsync(resolve, resourceGroup.Id, null, SecondTargetId));
        var load = Services.GetRequiredService<LoadResourcePlacementsInTransaction>();

        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var placements = await load(
            resourceGroup.TenantId,
            [first.RuntimeCellId, second.RuntimeCellId, ObjectId.GenerateNewId().ToString()],
            session);
        await session.CommitTransactionAsync();

        Assert.Equal(
            [first.RuntimeCellId, second.RuntimeCellId],
            placements.Select(placement => placement.RuntimeCellId)
                .OrderBy(id => id, StringComparer.Ordinal));
        Assert.All(placements, placement =>
            Assert.Equal(resourceGroup.Id, placement.ResourceGroupId));
    }

    [Fact]
    public async Task ConcurrentPlacementAndDeletion_DoNotLeaveActivePlacementUnderDeletedAggregate()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var resolve = Services.GetRequiredService<ResolveResourceGroupPlacement>();

        await Task.WhenAll(
            ResolveAsync(resolve, resourceGroup.Id, null, DefaultTargetId),
            Mediator.Send(new DeleteResourceGroupRequest(resourceGroup.Id, resourceGroup.TenantId)));

        await AssertDeletionFenceAsync(resourceGroup.Id);
    }

    [Fact]
    public async Task ConcurrentExistingCellReuseAndDeletion_AreParentFenced()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var resolve = Services.GetRequiredService<ResolveResourceGroupPlacement>();
        _ = AssertPlacement(await ResolveAsync(resolve, resourceGroup.Id, null, DefaultTargetId));

        await Task.WhenAll(
            ResolveAsync(resolve, resourceGroup.Id, null, DefaultTargetId),
            Mediator.Send(new DeleteResourceGroupRequest(resourceGroup.Id, resourceGroup.TenantId)));

        await AssertDeletionFenceAsync(resourceGroup.Id);
    }

    private TestDeploymentTargetLookup ConfigureTargets()
    {
        var targets = Services.GetRequiredService<TestDeploymentTargetLookup>();
        targets.AddEligibleTarget(SecondTargetId, "apps.test.local");
        return targets;
    }

    private async Task<Result<ResourcePlacement, ResourceGroupPlacementError>> ResolveAsync(
        ResolveResourceGroupPlacement resolve,
        string resourceGroupId,
        string? isolationScopeId,
        string? deploymentTargetId)
    {
        var transaction = new MongoRunTransaction(Fixture.Client);
        return await transaction.InvokeOutcomeAsync<Result<
            ResourcePlacement,
            ResourceGroupPlacementError>>(async (context, ct) =>
        {
            var result = await resolve(
                TestData.TenantId,
                resourceGroupId,
                isolationScopeId,
                deploymentTargetId,
                context.Session,
                ct);
            return result switch
            {
                Ok<ResourcePlacement> => context.Commit(result),
                Err<ResourceGroupPlacementError> => context.Abort(result)
            };
        });
    }

    private async Task<Result<IReadOnlyList<ResourcePlacement>, ResourceGroupPlacementError>>
        ResolveManyAsync(
            ResolveResourceGroupPlacements resolve,
            string resourceGroupId,
            string isolationScopeId,
            IReadOnlyList<string> deploymentTargetIds)
    {
        var transaction = new MongoRunTransaction(Fixture.Client);
        return await transaction.InvokeOutcomeAsync<Result<
            IReadOnlyList<ResourcePlacement>,
            ResourceGroupPlacementError>>(async (context, ct) =>
        {
            var result = await resolve(
                TestData.TenantId,
                resourceGroupId,
                isolationScopeId,
                deploymentTargetIds,
                context.Session,
                ct);
            return result switch
            {
                Ok<IReadOnlyList<ResourcePlacement>> => context.Commit(result),
                Err<ResourceGroupPlacementError> => context.Abort(result)
            };
        });
    }

    private async Task AssertDeletionFenceAsync(string resourceGroupId)
    {
        var savedGroup = await DbContext.ResourceGroups
            .Find(group => group.Id == resourceGroupId)
            .FirstOrDefaultAsync();
        var cells = await DbContext.RuntimeCells
            .Find(cell => cell.ResourceGroupId == resourceGroupId)
            .ToListAsync();
        if (savedGroup is null)
        {
            Assert.Empty(cells);
            return;
        }

        Assert.IsType<DeletingResourceGroupLifecycleState>(savedGroup.Lifecycle);
        Assert.All(cells, cell => Assert.IsType<DeletingRuntimeCellState>(cell.State));
    }

    private static ResourcePlacement AssertPlacement(
        Result<ResourcePlacement, ResourceGroupPlacementError> result) =>
        result switch
        {
            Ok<ResourcePlacement> success => success.Value,
            Err<ResourceGroupPlacementError> error => throw new Xunit.Sdk.XunitException(
                $"Expected placement, got {error.Value.GetType().Name}.")
        };

    private static IReadOnlyList<ResourcePlacement> AssertPlacements(
        Result<IReadOnlyList<ResourcePlacement>, ResourceGroupPlacementError> result) =>
        result switch
        {
            Ok<IReadOnlyList<ResourcePlacement>> success => success.Value,
            Err<ResourceGroupPlacementError> error => throw new Xunit.Sdk.XunitException(
                $"Expected placements, got {error.Value.GetType().Name}.")
        };

    private static ResourceGroupPlacementDeploymentTargetRejected
        AssertDeploymentTargetRejected(
            Result<ResourcePlacement, ResourceGroupPlacementError> result) =>
        result switch
        {
            Err<ResourceGroupPlacementError> error => error.Value switch
            {
                ResourceGroupPlacementDeploymentTargetRejected rejected => rejected,
                _ => throw new Xunit.Sdk.XunitException(
                    "Expected a deployment-target placement rejection.")
            },
            Ok<ResourcePlacement> => throw new Xunit.Sdk.XunitException(
                "Expected placement failure, got success.")
        };
}
