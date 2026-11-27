// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Sdk;

namespace Pole.Tests.Integration.ResourceGroups;

[Collection(IntegrationTestCollection.Tenant3)]
public sealed class ResourcePlacementIntegrationTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    private const string DefaultTargetId = TestData.DefaultDeploymentTargetId;
    private readonly string SecondTargetId = ObjectId.GenerateNewId().ToString();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
    }

    [Fact]
    public async Task ConcurrentResolve_CreatesOneCellReferenceAndInitialEvent()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var resolve = Services.GetRequiredService<ResolveResourceGroupPlacement>();
        var targets = Services.GetRequiredService<TestDeploymentTargetLookup>();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(_ => ResolveAsync(resolve, resourceGroup.Id, null, null)));

        var placements = results.Select(AssertPlacement).ToList();
        Assert.Single(placements.Select(placement => placement.RuntimeCellId).Distinct());
        Assert.Equal(
            1L,
            await DbContext.RuntimeCells.CountDocumentsAsync(cell => cell.ResourceGroupId == resourceGroup.Id));
        var referenceChange = Assert.Single(targets.ReferenceChanges);
        Assert.Equal(ResourcePlacementReferenceOwnerTypes.RuntimeCell, referenceChange.Owner.Type);
        Assert.Equal(placements[0].RuntimeCellId, referenceChange.Owner.Id);
        Assert.Equal([DefaultTargetId], referenceChange.AddedDeploymentTargetIds);
        Assert.Empty(referenceChange.RemovedDeploymentTargetIds);
        Assert.Equal(
            1L,
            await DbContext.CountOutboxMessagesAsync(
                Data.TenantId,
                "pole.resource-groups/v1/runtime-cell-reconciliation-requested"));
        var requestMessage = await DbContext.OutboxMessages
            .Find(message => message.TenantId == Data.TenantId &&
                message.EventType == "pole.resource-groups/v1/runtime-cell-reconciliation-requested")
            .SingleAsync();
        Assert.Equal(RuntimeCellPendingKey.Reconciliation(placements[0].RuntimeCellId, 1), requestMessage.PendingKey);
        Assert.IsType<PendingOutboxMessageState>(requestMessage.State);
    }

    [Fact]
    public async Task ExistingCell_RemainsUsable_WhenTargetRejectsNewWorkloads()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var targets = ConfigureTargets();
        var resolve = Services.GetRequiredService<ResolveResourceGroupPlacement>();
        var first = AssertPlacement(await ResolveAsync(resolve, resourceGroup.Id, null, DefaultTargetId));

        targets.RejectNewWorkloads = true;
        var existing = await ResolveAsync(resolve, resourceGroup.Id, null, DefaultTargetId);
        var rejectedNewTarget = await ResolveAsync(resolve, resourceGroup.Id, null, SecondTargetId);

        Assert.Equal(first.RuntimeCellId, AssertPlacement(existing).RuntimeCellId);
        _ = AssertDeploymentTargetRejected(rejectedNewTarget);
        Assert.Equal(
            1L,
            await DbContext.RuntimeCells.CountDocumentsAsync(cell => cell.ResourceGroupId == resourceGroup.Id));
    }

    [Fact]
    public async Task SameScope_UsesSameNamespaceName_InDifferentTargets()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        ConfigureTargets();
        var resolve = Services.GetRequiredService<ResolveResourceGroupPlacement>();

        var first = AssertPlacement(
            await ResolveAsync(resolve, resourceGroup.Id, resourceGroup.DefaultIsolationScopeId, DefaultTargetId));
        var second = AssertPlacement(
            await ResolveAsync(resolve, resourceGroup.Id, resourceGroup.DefaultIsolationScopeId, SecondTargetId));

        Assert.NotEqual(first.RuntimeCellId, second.RuntimeCellId);
        Assert.NotEqual(first.DeploymentTargetId, second.DeploymentTargetId);
        Assert.Equal(first.KubernetesNamespace, second.KubernetesNamespace);
    }

    [Fact]
    public async Task BatchResolve_CreatesCellsOnce_AndPreservesRequestedTargetOrder()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        ConfigureTargets();
        var resolve = Services.GetRequiredService<ResolveResourceGroupPlacements>();

        var placements = AssertPlacements(
            await ResolveManyAsync(
                resolve,
                resourceGroup.Id,
                resourceGroup.DefaultIsolationScopeId,
                [SecondTargetId, DefaultTargetId]));

        Assert.Equal([SecondTargetId, DefaultTargetId], placements.Select(placement => placement.DeploymentTargetId));
        Assert.Equal(
            2L,
            await DbContext.RuntimeCells.CountDocumentsAsync(cell => cell.ResourceGroupId == resourceGroup.Id));
        Assert.Equal(
            2L,
            await DbContext.CountOutboxMessagesAsync(
                Data.TenantId,
                "pole.resource-groups/v1/runtime-cell-reconciliation-requested"));
    }

    [Fact]
    public async Task LoadMany_ReturnsRequestedTenantScopedPlacementSnapshots()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        ConfigureTargets();
        var resolve = Services.GetRequiredService<ResolveResourceGroupPlacement>();
        var first = AssertPlacement(await ResolveAsync(resolve, resourceGroup.Id, null, DefaultTargetId));
        var second = AssertPlacement(await ResolveAsync(resolve, resourceGroup.Id, null, SecondTargetId));
        var load = Services.GetRequiredService<LoadResourcePlacementsInTransaction>();

        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var placements = await load(
            new LoadResourcePlacementsInTransactionRequest(
                TenantId: resourceGroup.TenantId,
                RuntimeCellIds: [first.RuntimeCellId, second.RuntimeCellId, ObjectId.GenerateNewId().ToString()]),
            session: session);
        await session.CommitTransactionAsync();

        Assert.Equal(
            [first.RuntimeCellId, second.RuntimeCellId],
            placements.Select(placement => placement.RuntimeCellId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.All(placements, placement => Assert.Equal(resourceGroup.Id, placement.ResourceGroupId));
    }

    [Fact]
    public async Task ConcurrentPlacementAndDeletion_DoNotLeaveActivePlacementUnderDeletedAggregate()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var resolve = Services.GetRequiredService<ResolveResourceGroupPlacement>();

        await Task.WhenAll(
            ResolveAsync(resolve, resourceGroup.Id, null, DefaultTargetId),
            Mediator.Send(new DeleteResourceGroupRequest(resourceGroup.Id, resourceGroup.TenantId)));

        await AssertDeletionFenceAsync(resourceGroup.Id);
    }

    [Fact]
    public async Task ConcurrentExistingCellReuseAndDeletion_AreParentFenced()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
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

    private async Task<ResolveResourceGroupPlacementOutcome> ResolveAsync(
        ResolveResourceGroupPlacement resolve,
        string resourceGroupId,
        string? isolationScopeId,
        string? deploymentTargetId)
    {
        var transaction = new MongoRunTransaction(Fixture.Client);
        return await transaction.InvokeOutcomeAsync<ResolveResourceGroupPlacementOutcome>(async (context, ct) =>
        {
            var result = await resolve(
                new ResolveResourceGroupPlacementRequest(
                    TenantId: Data.TenantId,
                    ResourceGroupId: resourceGroupId,
                    IsolationScopeId: isolationScopeId,
                    DeploymentTargetId: deploymentTargetId),
                session: context.Session,
                cancellationToken: ct);
            return result switch
            {
                ResourcePlacement => context.Commit(result),
                ResourceGroupPlacementError => context.Abort(result),
            };
        });
    }

    private async Task<ResolveResourceGroupPlacementsOutcome> ResolveManyAsync(
        ResolveResourceGroupPlacements resolve,
        string resourceGroupId,
        string isolationScopeId,
        IReadOnlyList<string> deploymentTargetIds)
    {
        var transaction = new MongoRunTransaction(Fixture.Client);
        return await transaction.InvokeOutcomeAsync<ResolveResourceGroupPlacementsOutcome>(async (context, ct) =>
        {
            var result = await resolve(
                new ResolveResourceGroupPlacementsRequest(
                    TenantId: Data.TenantId,
                    ResourceGroupId: resourceGroupId,
                    IsolationScopeId: isolationScopeId,
                    DeploymentTargetIds: deploymentTargetIds),
                session: context.Session,
                cancellationToken: ct);
            return result switch
            {
                ResourcePlacements => context.Commit(result),
                ResourceGroupPlacementError => context.Abort(result),
            };
        });
    }

    private async Task AssertDeletionFenceAsync(string resourceGroupId)
    {
        var savedGroup = await DbContext.ResourceGroups
            .Find(group => group.Id == resourceGroupId)
            .FirstOrDefaultAsync();
        var cells = await DbContext.RuntimeCells.Find(cell => cell.ResourceGroupId == resourceGroupId).ToListAsync();
        if (savedGroup is null)
        {
            Assert.Empty(cells);
            return;
        }

        Assert.IsType<DeletingResourceGroupLifecycleState>(savedGroup.Lifecycle);
        Assert.All(cells, cell => Assert.IsType<DeletingRuntimeCellState>(cell.State));
    }

    private static ResourcePlacement AssertPlacement(ResolveResourceGroupPlacementOutcome result) => result switch
    {
        ResourcePlacement success => success,
        ResourceGroupPlacementError error => throw new XunitException(
            $"Expected placement, got {error.Value.GetType().Name}."),
    };

    private static IReadOnlyList<ResourcePlacement> AssertPlacements(ResolveResourceGroupPlacementsOutcome result) =>
        result switch
        {
            ResourcePlacements success => success.Items,
            ResourceGroupPlacementError error => throw new XunitException(
                $"Expected placements, got {error.Value.GetType().Name}."),
        };

    private static ResourceGroupPlacementDeploymentTargetRejected AssertDeploymentTargetRejected(
        ResolveResourceGroupPlacementOutcome result) => result switch
    {
        ResourceGroupPlacementError error => error.Value switch
        {
            ResourceGroupPlacementDeploymentTargetRejected rejected => rejected,
            _ => throw new XunitException("Expected a deployment-target placement rejection."),
        },
        ResourcePlacement => throw new XunitException("Expected placement failure, got success."),
    };
}
