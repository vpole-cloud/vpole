using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Application.Persistence;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Request;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceRegistrations;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using Pole.Workloads;

namespace Pole.Tests.Integration.ResourceGroups;

public sealed class ResourceGroupIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Create_PersistsResourceGroup_AndWritesOutboxMessage()
    {
        var request = TestData.CreateResourceGroupRequest();

        var result = await Mediator.Send(request);

        var created = result.AssertSuccess();

        var saved = await DbContext.ResourceGroups.Find(group => group.Id == created.Id).FirstOrDefaultAsync();
        Assert.NotNull(saved);
        Assert.Equal(request.Body.Name, saved.Name);
        Assert.Equal(created.DefaultIsolationScopeId, saved.DefaultIsolationScopeId);
        Assert.Equal("active", created.Lifecycle);
        Assert.Equal(saved.Revision, created.Revision);

        var defaultScope = await DbContext.IsolationScopes
            .Find(scope => scope.ResourceGroupId == saved.Id)
            .SingleOrDefaultAsync();
        Assert.NotNull(defaultScope);
        Assert.Equal(saved.DefaultIsolationScopeId, defaultScope.Id);
        Assert.True(defaultScope.IsDefault);
        Assert.Equal(IsolationScopeFactory.DefaultScopeName, defaultScope.Name);

        var outboxCount = await DbContext.CountOutboxMessagesAsync("resource-group-created");
        Assert.Equal(1L, outboxCount);
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync("isolation-scope-created"));
    }

    [Fact]
    public async Task Create_DoesNotRequireADeploymentTargetThatAcceptsWorkloads()
    {
        Services.GetRequiredService<TestDeploymentTargetLookup>().RejectNewWorkloads = true;

        var created = (await Mediator.Send(TestData.CreateResourceGroupRequest("logical-only")))
            .AssertSuccess();

        Assert.Equal("logical-only", created.Name);
        Assert.Equal(0L, await DbContext.RuntimeCells.CountDocumentsAsync(
            Builders<RuntimeCell>.Filter.Empty));
    }

    [Fact]
    public async Task Get_ReturnsSeededGroups_AndSupportsSearch()
    {
        await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("alpha"));
        await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("beta"));

        var result = await Mediator.Send(new GetResourceGroupsRequest
        {
            TenantId = TestData.TenantId,
            Search = "alp",
            PageNumber = 1,
            PageSize = 10
        });

        var response = result.AssertHandlerOutcome() switch
        {
            PaginatedResponse<ResourceGroupDto> success => success
        };
        Assert.Single(response.Items);
        Assert.Equal("alpha", response.Items.Single().Name);
    }

    [Fact]
    public async Task Delete_RemovesResourceGroup_AndWritesDeleteEvent()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());

        var result = await Mediator.Send(new DeleteResourceGroupRequest(
            resourceGroup.Id,
            resourceGroup.TenantId));

        Assert.True(result.AssertHandlerOutcome() is ResourceGroupDeletionCompleted);

        var deleted = await DbContext.ResourceGroups.Find(group => group.Id == resourceGroup.Id).FirstOrDefaultAsync();
        Assert.Null(deleted);
        Assert.False(await DbContext.IsolationScopes
            .Find(scope => scope.ResourceGroupId == resourceGroup.Id)
            .AnyAsync());

        var outboxCount = await DbContext.CountOutboxMessagesAsync("resource-group-deleted");
        Assert.Equal(1L, outboxCount);
    }

    [Fact]
    public async Task Delete_ReturnsConflict_WhenResourcesAreRegistered()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        await DbContext.ResourceRegistrations.InsertOneAsync(ResourceRegistrationFactory.Create(
            resourceGroup.TenantId,
            resourceGroup.Id,
            ResourceTypeId.Create("pole/web-service"),
            "507f1f77bcf86cd799439081",
            "registered-resource",
            owningPluginId: null,
            new ResourceRegistrationFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                DateTime.UtcNow)));

        var result = await Mediator.Send(new DeleteResourceGroupRequest(
            resourceGroup.Id,
            resourceGroup.TenantId));

        var error = result.AssertHandlerOutcome() switch
        {
            ResourceGroupDeletionError failure => failure,
            _ => throw new Xunit.Sdk.XunitException("Expected resource-group deletion failure.")
        };
        Assert.True(error is ResourceGroupContainsResources);
        Assert.True(await DbContext.ResourceGroups
            .Find(group => group.Id == resourceGroup.Id)
            .AnyAsync());
    }

    [Fact]
    public async Task Delete_RetriesAndReturnsConflict_WhenRegistrationCommitsAfterEmptyRead()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var guard = new OneShotPausingDeploymentGuard();
        var beginDeletion = new MongoBeginResourceGroupDeletion(
            DbContext.ResourceGroups,
            DbContext.IsolationScopes,
            DbContext.RuntimeCells,
            DbContext.ResourceRegistrations,
            guard.InvokeAsync,
            Services.GetRequiredService<StoreOutboxWrites>(),
            Services.GetRequiredService<
                RunMongoOutcomeTransaction<BeginResourceGroupDeletionOutcome>>(),
            TimeProvider.System);
        var registrar = Services.GetRequiredService<IResourceRegistrar>();

        var deletion = beginDeletion.InvokeAsync(
            resourceGroup.TenantId,
            resourceGroup.Id);
        try
        {
            await guard.WaitUntilCheckedAsync().WaitAsync(TestTimeout);
            using var session = await DbContext.Client.StartSessionAsync();
            session.StartTransaction();
            (await registrar.RegisterAsync(
                resourceGroup.TenantId,
                resourceGroup.Id,
                new ResourceRegistrationDescriptor(
                    ResourceTypeId.Create("pole/web-service"),
                    "racing-resource",
                    "Racing resource"),
                session)).AssertSuccess();
            await session.CommitTransactionAsync();
        }
        finally
        {
            guard.Release();
        }

        var result = await deletion.WaitAsync(TestTimeout);
        var error = result switch
        {
            ResourceGroupDeletionError failure => failure,
            _ => throw new Xunit.Sdk.XunitException(
                "Expected resource-group deletion failure after transaction retry.")
        };
        Assert.True(error is ResourceGroupContainsResources);
        Assert.True(await DbContext.ResourceGroups
            .Find(group => group.Id == resourceGroup.Id)
            .AnyAsync());
        Assert.True(await DbContext.ResourceRegistrations
            .Find(registration => registration.ResourceId == "racing-resource")
            .AnyAsync());
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "resource-group-deleted"));
    }

    [Fact]
    public async Task Delete_WithRuntimeCell_RequestsAggregateDeletion()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var cell = RuntimeCellFactory.Create(
            resourceGroup.TenantId,
            resourceGroup.Id,
            resourceGroup.DefaultIsolationScopeId,
            "507f1f77bcf86cd799439071",
            "pole-delete-resource-group",
            RuntimeCellFactory.DefaultOptions);
        await DbContext.RuntimeCells.InsertOneAsync(cell);

        var result = await Mediator.Send(new DeleteResourceGroupRequest(
            resourceGroup.Id,
            resourceGroup.TenantId));

        Assert.True(result.AssertHandlerOutcome() is ResourceGroupDeletionRequested);
        var savedGroup = await DbContext.ResourceGroups
            .Find(group => group.Id == resourceGroup.Id)
            .SingleAsync();
        Assert.IsType<DeletingResourceGroupLifecycleState>(savedGroup.Lifecycle);
        Assert.Equal("deleting", savedGroup.ToDto(resourceCount: 0).Lifecycle);
        var savedScope = await DbContext.IsolationScopes
            .Find(scope => scope.Id == resourceGroup.DefaultIsolationScopeId)
            .SingleAsync();
        Assert.IsType<DeletingIsolationScopeLifecycleState>(savedScope.Lifecycle);
        var savedCell = await DbContext.RuntimeCells
            .Find(item => item.Id == cell.Id)
            .SingleAsync();
        Assert.IsType<DeletingRuntimeCellState>(savedCell.State);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "runtime-cell-deletion-requested"));
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "resource-group-deleted"));
    }

    [Fact]
    public async Task OwnershipLookup_RejectsDeletingResourceGroup()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        await DbContext.ResourceGroups.ReplaceOneAsync(
            group => group.Id == resourceGroup.Id,
            resourceGroup.RequestDeletion(resourceGroup.UpdatedAtUtc.AddMinutes(1)));
        var lookup = Services.GetRequiredService<GetResourceGroupOwnership>();

        var result = await lookup(
            resourceGroup.TenantId,
            resourceGroup.Id);

        var failure = result switch
        {
            Err<ResourceGroupOwnershipError> error => error.Value,
            _ => throw new Xunit.Sdk.XunitException("Expected resource-group ownership failure.")
        };
        Assert.True(failure is ResourceGroupOwnershipDeleting);
    }

    private sealed class OneShotPausingDeploymentGuard
    {
        private readonly TaskCompletionSource _checked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocationCount;

        public Task WaitUntilCheckedAsync() => _checked.Task;

        public void Release() => _release.TrySetResult();

        public async Task<Result<Unit, ResourceGroupHasUnfinishedDeployments>> InvokeAsync(
            string tenantId,
            string resourceGroupId,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            _ = tenantId;
            _ = resourceGroupId;
            _ = session;
            if (Interlocked.Increment(ref _invocationCount) == 1)
            {
                _checked.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return new Ok<Unit>(Unit.Value);
        }
    }
}
