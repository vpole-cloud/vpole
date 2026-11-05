using Pole.Features.GitOps.Compiler;
using System.Net;
using Pole.Tests.Integration.Http;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Tenants.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class RepositorySourceApiTests
{
    [Fact]
    public async Task Inventory_pages_return_stable_metadata_and_the_source_inventory_stamp()
    {
        var source = await Create(files: [GroupManifest("beta"), GroupManifest("alpha")]);
        await Run(source.Id);
        var current = (await Read(source.Id))!;
        var first = (await Client.GetFromJsonAsync<RepositoryResourcePage>(
            $"{Root}/{source.Id}/resources?pageSize=1"))!;
        var second = (await Client.GetFromJsonAsync<RepositoryResourcePage>(
            $"{Root}/{source.Id}/resources?page=2&pageSize=1"))!;
        Assert.Equal(current.Inventory.Revision, first.InventoryRevision);
        Assert.Equal(first.InventoryRevision, second.InventoryRevision);
        Assert.True(first.InventoryRevision > 0);
        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Equal("alpha", Assert.Single(first.Items).Name);
        Assert.Equal("beta", Assert.Single(second.Items).Name);
        Assert.All(first.Items.Concat(second.Items), item =>
        {
            Assert.Equal("ResourceGroup", item.Kind);
            Assert.Equal("pole/resource-group", item.ResourceTypeId);
            Assert.Equal(GitResourceMaterialization.Created, item.Materialization);
            Assert.False(item.PendingRemoval);
            Assert.Equal(current.Synchronization.LatestRunId, item.LastRunId);
        });
        using var response = await Client.GetAsync($"{Root}/{source.Id}/resources");
        var json = await response.Content.ReadAsStringAsync();
        foreach (var field in new[] { "projections", "desiredHash", "appliedHash", "spec", "credentials" })
            Assert.DoesNotContain(field, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resource_group_view_metadata_does_not_make_it_a_registered_child_resource()
    {
        using var scope = ApiServices.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<DeclarativeProviderRegistry>().Find("ResourceGroup");
        var view = Assert.IsAssignableFrom<IDeclarativeResourceViewProvider>(provider);
        Assert.Equal("pole/resource-group", view.ResourceType.Value);
        Assert.False(provider is IDeclarativeRegisteredResourceProvider);
    }

    [Fact]
    public async Task Inventory_keeps_reserved_and_pending_identity_metadata_without_assuming_resource_existence()
    {
        var source = await Create(files: [GroupManifest("retained")]);
        await Run(source.Id);
        using var scope = ApiServices.CreateScope();
        var inventory = scope.ServiceProvider.GetRequiredService<IMongoCollection<ManagedDeclaration>>();
        var original = await inventory.Find(item => item.SourceId == source.Id).SingleAsync();
        await inventory.ReplaceOneAsync(item => item.Id == original.Id,
            original with { PendingRemoval = true, Blocker = "DeclarationManagedResourceMissing" });
        var reserved = original with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Kind = "ThirdParty",
            Name = "reserved",
            ResourceId = ObjectId.GenerateNewId().ToString(),
            Materialization = GitResourceMaterialization.Allocated,
            PendingRemoval = false,
            Blocker = "AwaitingDependency"
        };
        await inventory.InsertOneAsync(reserved);
        var rows = (await Client.GetFromJsonAsync<RepositoryResourcePage>($"{Root}/{source.Id}/resources"))!.Items;
        var retained = Assert.Single(rows, item => item.Name == "retained");
        Assert.True(retained.PendingRemoval);
        Assert.Equal("DeclarationManagedResourceMissing", retained.Blocker);
        var allocated = Assert.Single(rows, item => item.Name == "reserved");
        Assert.Equal(GitResourceMaterialization.Allocated, allocated.Materialization);
        Assert.Null(allocated.ResourceTypeId);
        Assert.Equal("AwaitingDependency", allocated.Blocker);
    }

    [Fact]
    public async Task Inventory_revision_changes_are_visible_between_pages_after_an_identity_release()
    {
        var source = await Create(files: [GroupManifest("old")]);
        await Run(source.Id);
        var before = (await Client.GetFromJsonAsync<RepositoryResourcePage>($"{Root}/{source.Id}/resources"))!;
        using var scope = ApiServices.CreateScope();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        await scope.ServiceProvider.GetRequiredService<IMongoCollection<ManagedDeclaration>>()
            .DeleteManyAsync(session, item => item.SourceId == source.Id);
        await scope.ServiceProvider.GetRequiredService<IMongoCollection<GitSynchronizationState>>()
            .UpdateOneAsync(session, item => item.Source.SourceId == source.Id,
                Builders<GitSynchronizationState>.Update.Inc(item => item.InventoryRevision, 1));
        await session.CommitTransactionAsync();
        var after = (await Client.GetFromJsonAsync<RepositoryResourcePage>($"{Root}/{source.Id}/resources"))!;
        Assert.Empty(after.Items);
        Assert.Equal(before.InventoryRevision + 1, after.InventoryRevision);
        Assert.Equal(after.InventoryRevision, (await Read(source.Id))!.Inventory.Revision);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=1000001")]
    public async Task Inventory_rejects_unbounded_or_invalid_paging(string query)
    {
        var source = await Create();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Client.GetAsync($"{Root}/{source.Id}/resources?{query}")).StatusCode);
    }

    [Fact]
    public async Task Inventory_allows_viewers_but_never_returns_another_tenants_source_or_rows()
    {
        var source = await Create(files: [GroupManifest("private")]);
        await Run(source.Id);
        var tenant = await Fixture.DbContext.Tenants.Find(item => item.Id == TestData.TenantId).SingleAsync();
        await Fixture.DbContext.Tenants.ReplaceOneAsync(item => item.Id == tenant.Id, tenant with
        {
            Members = [TenantFactory.CreateMember(tenant.Id, TestData.UserId, TestData.UserEmail, TenantRole.Viewer)]
        });
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"{Root}/{source.Id}/resources")).StatusCode);
        var foreign = TenantFactory.Create("Foreign", ObjectId.GenerateNewId().ToString());
        foreign = foreign with
        {
            Members = [TenantFactory.CreateMember(foreign.Id, TestData.UserId, TestData.UserEmail, TenantRole.Owner)]
        };
        await Fixture.DbContext.Tenants.InsertOneAsync(foreign);
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, foreign.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"{Root}/{source.Id}/resources")).StatusCode);
    }
}
