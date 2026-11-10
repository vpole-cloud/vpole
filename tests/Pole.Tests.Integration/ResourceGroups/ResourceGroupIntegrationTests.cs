using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Request;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Sdk;

namespace Pole.Tests.Integration.ResourceGroups;

[Collection(IntegrationTestCollection.Tenant3)]
public sealed class ResourceGroupIntegrationTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Create_PersistsResourceGroup_AndWritesOutboxMessage()
    {
        var request = Data.CreateResourceGroupRequest();

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

        var outboxCount = await DbContext.CountOutboxMessagesAsync(
            Data.TenantId,
            "pole.resource-groups/v1/resource-group-created");
        Assert.Equal(1L, outboxCount);
        Assert.Equal(
            0L,
            await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.resource-groups/v1/isolation-scope-created"));
    }

    [Fact]
    public async Task Create_DoesNotRequireADeploymentTargetThatAcceptsWorkloads()
    {
        Services.GetRequiredService<TestDeploymentTargetLookup>().RejectNewWorkloads = true;

        var created = (await Mediator.Send(Data.CreateResourceGroupRequest("logical-only"))).AssertSuccess();

        Assert.Equal("logical-only", created.Name);
        Assert.Equal(
            0L,
            await DbContext.RuntimeCells.CountDocumentsAsync(
                Builders<RuntimeCell>.Filter.Eq(item => item.TenantId, Data.TenantId)));
    }

    [Fact]
    public async Task Get_ReturnsSeededGroups_AndSupportsSearch()
    {
        await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("beta"));

        var result = await Mediator.Send(
            new GetResourceGroupsRequest
            {
                TenantId = Data.TenantId,
                Search = "alp",
                PageNumber = 1,
                PageSize = 10,
            });

        var response = result.AssertHandlerOutcome() switch
        {
            PaginatedResponse<ResourceGroupDto> success => success,
        };
        Assert.Single(response.Items);
        Assert.Equal("alpha", response.Items.Single().Name);
    }

    [Fact]
    public async Task Delete_RemovesResourceGroup_AndWritesDeleteEvent()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());

        var result = await Mediator.Send(new DeleteResourceGroupRequest(resourceGroup.Id, resourceGroup.TenantId));

        Assert.True(result.AssertHandlerOutcome() is ResourceGroupDeletionCompleted);

        var deleted = await DbContext.ResourceGroups.Find(group => group.Id == resourceGroup.Id).FirstOrDefaultAsync();
        Assert.Null(deleted);
        Assert.False(
            await DbContext.IsolationScopes.Find(scope => scope.ResourceGroupId == resourceGroup.Id).AnyAsync());

        var outboxCount = await DbContext.CountOutboxMessagesAsync(
            Data.TenantId,
            "pole.resource-groups/v1/resource-group-deleted");
        Assert.Equal(1L, outboxCount);
    }

    [Fact]
    public async Task Delete_ReturnsConflict_WhenResourcesAreRegistered()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        await DbContext.ResourceRegistrations.InsertOneAsync(
            ResourceRegistrationFactory.Create(
                resourceGroup.TenantId,
                resourceGroup.Id,
                ResourceTypeId.Create("pole/web-service"),
                "507f1f77bcf86cd799439081",
                "registered-resource",
                owningPluginId: null,
                new ResourceRegistrationFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow)));

        var result = await Mediator.Send(new DeleteResourceGroupRequest(resourceGroup.Id, resourceGroup.TenantId));

        var error = result.AssertHandlerOutcome() switch
        {
            ResourceGroupDeletionError failure => failure,
            _ => throw new XunitException("Expected resource-group deletion failure."),
        };
        Assert.True(error is ResourceGroupContainsResources);
        Assert.True(await DbContext.ResourceGroups.Find(group => group.Id == resourceGroup.Id).AnyAsync());
    }

    [Fact]
    public async Task Delete_RetriesAndReturnsConflict_WhenRegistrationCommitsAfterEmptyRead()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var guard = new OneShotPausingDeploymentGuard();
        var beginDeletion = new MongoBeginResourceGroupDeletion(
            DbContext.ResourceGroups,
            DbContext.IsolationScopes,
            DbContext.RuntimeCells,
            DbContext.ResourceRegistrations,
            guard.InvokeAsync,
            Services.GetRequiredService<StoreOutboxWrites>(),
            Services.GetRequiredService<RunMongoOutcomeTransaction<BeginResourceGroupDeletionOutcome>>(),
            TimeProvider.System,
            Services.GetRequiredService<IGitOpsMutations>());
        var registrar = Services.GetRequiredService<IResourceRegistrar>();

        var deletion = beginDeletion.InvokeAsync(resourceGroup.TenantId, resourceGroup.Id);
        try
        {
            await guard.WaitUntilCheckedAsync().WaitAsync(TestTimeout);
            using var session = await DbContext.Client.StartSessionAsync();
            session.StartTransaction();
            Assert.IsType<RegisteredResource>(
                (await registrar.RegisterAsync(
                    new RegisterResourceRequest(
                        TenantId: resourceGroup.TenantId,
                        ResourceGroupId: resourceGroup.Id,
                        Resource: new ResourceRegistrationDescriptor(
                            ResourceTypeId.Create("pole/web-service"),
                            "racing-resource",
                            "Racing resource")),
                    session: session)).Value);
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
            _ => throw new XunitException("Expected resource-group deletion failure after transaction retry."),
        };
        Assert.True(error is ResourceGroupContainsResources);
        Assert.True(await DbContext.ResourceGroups.Find(group => group.Id == resourceGroup.Id).AnyAsync());
        Assert.True(
            await DbContext.ResourceRegistrations
                .Find(registration =>
                    registration.TenantId == Data.TenantId && registration.ResourceId == "racing-resource")
                .AnyAsync());
        Assert.Equal(
            0L,
            await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.resource-groups/v1/resource-group-deleted"));
    }

    [Fact]
    public async Task Delete_WithRuntimeCell_RequestsAggregateDeletion()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var cell = RuntimeCellFactory.Create(
            resourceGroup.TenantId,
            resourceGroup.Id,
            resourceGroup.DefaultIsolationScopeId,
            "507f1f77bcf86cd799439071",
            "pole-delete-resource-group",
            RuntimeCellFactory.DefaultOptions);
        await DbContext.RuntimeCells.InsertOneAsync(cell);

        var result = await Mediator.Send(new DeleteResourceGroupRequest(resourceGroup.Id, resourceGroup.TenantId));

        Assert.True(result.AssertHandlerOutcome() is ResourceGroupDeletionRequested);
        var savedGroup = await DbContext.ResourceGroups.Find(group => group.Id == resourceGroup.Id).SingleAsync();
        Assert.IsType<DeletingResourceGroupLifecycleState>(savedGroup.Lifecycle);
        Assert.Equal("deleting", savedGroup.ToDto(resourceCount: 0).Lifecycle);
        var savedScope = await DbContext.IsolationScopes
            .Find(scope => scope.Id == resourceGroup.DefaultIsolationScopeId)
            .SingleAsync();
        Assert.IsType<DeletingIsolationScopeLifecycleState>(savedScope.Lifecycle);
        var savedCell = await DbContext.RuntimeCells.Find(item => item.Id == cell.Id).SingleAsync();
        Assert.IsType<DeletingRuntimeCellState>(savedCell.State);
        Assert.Equal(
            1L,
            await DbContext.CountOutboxMessagesAsync(
                Data.TenantId,
                "pole.resource-groups/v1/runtime-cell-deletion-requested"));
        Assert.Equal(
            0L,
            await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.resource-groups/v1/resource-group-deleted"));
    }

    [Fact]
    public async Task OwnershipLookup_RejectsDeletingResourceGroup()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        await DbContext.ResourceGroups.ReplaceOneAsync(
            group => group.Id == resourceGroup.Id,
            resourceGroup.RequestDeletion(resourceGroup.UpdatedAtUtc.AddMinutes(1)));
        var lookup = Services.GetRequiredService<GetResourceGroupOwnership>();

        var result = await lookup(resourceGroup.TenantId, resourceGroup.Id);

        var failure = Assert.IsType<ResourceGroupOwnershipError>(result.Value);
        Assert.IsType<ResourceGroupOwnershipDeleting>(failure.Value);
    }

    private sealed class OneShotPausingDeploymentGuard
    {
        private readonly TaskCompletionSource _checked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _invocationCount;

        public Task WaitUntilCheckedAsync() => _checked.Task;

        public void Release() => _release.TrySetResult();

        public async Task<EnsureResourceGroupDeploymentsSettledOutcome> InvokeAsync(
            EnsureNoUnfinishedResourceGroupDeploymentsRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            _ = request.TenantId;
            _ = request.ResourceGroupId;
            _ = session;
            if (Interlocked.Increment(ref _invocationCount) == 1)
            {
                _checked.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return new ResourceGroupDeploymentsSettled();
        }
    }
}
