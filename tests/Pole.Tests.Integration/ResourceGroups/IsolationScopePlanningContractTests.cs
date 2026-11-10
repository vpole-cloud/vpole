using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceGroups;

[Collection(IntegrationTestCollection.Tenant3)]
public sealed class IsolationScopePlanningContractTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task NamedAndPinnedScopeReadsRespectTenantGroupAndRealFence()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var scope = IsolationScopeFactory.CreateAdditional(
            group.TenantId,
            group.Id,
            "isolated",
            IsolationScopeFactory.DefaultOptions);
        await DbContext.IsolationScopes.InsertOneAsync(scope);
        var resolve = Services.GetRequiredService<ResolveNamedIsolationScope>();
        var named = Assert.IsType<IsolationScopePlanningSnapshot>(
            (await resolve(new NamedIsolationScopeRequest(group.TenantId, group.Id, scope.Name), default)).Value);
        Assert.Equal(scope.Id, named.IsolationScopeId);
        var otherTenant = ObjectId.GenerateNewId().ToString();
        Assert.IsType<IsolationScopePlanningUnavailable>(
            (await resolve(new NamedIsolationScopeRequest(otherTenant, group.Id, scope.Name), default)).Value);
        var defaultScope = Assert.IsType<IsolationScopePlanningSnapshot>(
            (await resolve(new NamedIsolationScopeRequest(group.TenantId, group.Id, null), default)).Value);
        Assert.Equal(group.DefaultIsolationScopeId, defaultScope.IsolationScopeId);

        using var session = await Fixture.Client.StartSessionAsync();
        var read = Services.GetRequiredService<LoadIsolationScopePlanningSnapshot>();
        var fence = Services.GetRequiredService<FenceIsolationScopePlanningSnapshot>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await read(
            new IsolationScopePlanningRequest(group.TenantId, group.Id, scope.Id),
            session,
            default));
        session.StartTransaction();
        Assert.Equal(
            named,
            Assert.IsType<IsolationScopePlanningSnapshot>(
                (await read(new IsolationScopePlanningRequest(group.TenantId, group.Id, scope.Id), session, default))
                .Value));
        Assert.IsType<IsolationScopePlanningFenceRejected>(
            (await fence(named with { ScopeRevision = 99 }, session, default)).Value);
        Assert.IsType<IsolationScopePlanningFenced>((await fence(named, session, default)).Value);
        await session.CommitTransactionAsync();
        var currentGroup = await DbContext.ResourceGroups.Find(item => item.Id == group.Id).SingleAsync();
        Assert.Equal(group.RegistrationFence + 1, currentGroup.RegistrationFence);
        Assert.Equal(group.Revision, currentGroup.Revision);
        Assert.Equal(
            new DateTimeOffset(group.UpdatedAtUtc).ToUnixTimeMilliseconds(),
            new DateTimeOffset(currentGroup.UpdatedAtUtc).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task AdditionalNamesAreTenantWideWhileDefaultScopesRemainPerGroup()
    {
        var first = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("first"));
        var second = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("second"));
        var outcomes = await Task.WhenAll(
            Mediator.Send(
                new CreateIsolationScopeRequest(
                    first.Id,
                    new CreateIsolationScopeRequestBody("shared-name"),
                    first.TenantId)),
            Mediator.Send(
                new CreateIsolationScopeRequest(
                    second.Id,
                    new CreateIsolationScopeRequestBody("shared-name"),
                    second.TenantId)));
        Assert.Single(outcomes, result => result.AssertHandlerOutcome().Value is IsolationScopeDto);
        Assert.Single(outcomes, result => result.AssertHandlerOutcome().Value is CreateIsolationScopeError);
        Assert.Equal(
            2,
            await DbContext.IsolationScopes.CountDocumentsAsync(item =>
                item.TenantId == Data.TenantId && item.IsDefault));
        Assert.Equal(
            1,
            await DbContext.IsolationScopes.CountDocumentsAsync(item =>
                item.TenantId == Data.TenantId && item.Name == "shared-name"));
        Assert.Equal(
            1,
            await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.resource-groups/v1/isolation-scope-created"));
        var revisions = await DbContext.ResourceGroups
            .Find(item => item.Id == first.Id || item.Id == second.Id)
            .Project(item => item.Revision)
            .ToListAsync();
        Assert.Equal([1, 2], revisions.Order().ToArray());
    }
}
