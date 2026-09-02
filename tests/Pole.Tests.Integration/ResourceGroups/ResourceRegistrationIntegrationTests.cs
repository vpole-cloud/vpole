using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Application.Migrations;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Request;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.ResourceGroups;

public sealed class ResourceRegistrationIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private static readonly ResourceTypeId WebServiceType =
        ResourceTypeId.Create("pole/web-service");

    [Fact]
    public async Task Register_IsIdempotent_AndRejectsCrossGroupIdentityReuse()
    {
        var firstGroup = await DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup("first"));
        var secondGroup = await DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup("second"));
        var register = Services.GetRequiredService<RegisterResource>();
        var descriptor = new ResourceRegistrationDescriptor(
            WebServiceType,
            "resource-1",
            "Web API");

        var first = await InTransactionAsync((session, cancellationToken) =>
            register(
                firstGroup.TenantId,
                firstGroup.Id,
                descriptor,
                session,
                cancellationToken));
        var repeated = await InTransactionAsync((session, cancellationToken) =>
            register(
                firstGroup.TenantId,
                firstGroup.Id,
                descriptor,
                session,
                cancellationToken));
        var conflict = await InTransactionAsync((session, cancellationToken) =>
            register(
                secondGroup.TenantId,
                secondGroup.Id,
                descriptor,
                session,
                cancellationToken),
            commitOnError: false);

        Assert.Equal(
            first.AssertSuccess().RegistrationId,
            repeated.AssertSuccess().RegistrationId);
        Assert.IsType<ResourceRegistrationIdentityConflict>(conflict.AssertFailure().Value);
        Assert.Equal(1L, await DbContext.ResourceRegistrations.CountDocumentsAsync(
            Builders<ResourceRegistration>.Filter.Empty));
    }

    [Fact]
    public async Task Register_RollsBackWithOwningTransaction_AndIsTenantScoped()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var register = Services.GetRequiredService<RegisterResource>();
        using (var session = await DbContext.Client.StartSessionAsync())
        {
            session.StartTransaction();
            (await register(
                group.TenantId,
                group.Id,
                new ResourceRegistrationDescriptor(
                    WebServiceType,
                    "rolled-back",
                    "Rolled back"),
                session)).AssertSuccess();
            await session.AbortTransactionAsync();
        }

        Assert.False(await DbContext.ResourceRegistrations
            .Find(registration => registration.ResourceId == "rolled-back")
            .AnyAsync());
        var unchangedGroup = await DbContext.ResourceGroups
            .Find(candidate => candidate.Id == group.Id)
            .SingleAsync();
        Assert.Equal(group.RegistrationFence, unchangedGroup.RegistrationFence);

        using var inactiveSession = await DbContext.Client.StartSessionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => register(
            group.TenantId,
            group.Id,
            new ResourceRegistrationDescriptor(WebServiceType, "invalid", "Invalid"),
            inactiveSession));

        var crossTenant = await InTransactionAsync((session, cancellationToken) =>
            register(
                ObjectId.GenerateNewId().ToString(),
                group.Id,
                new ResourceRegistrationDescriptor(WebServiceType, "cross-tenant", "Invalid"),
                session,
                cancellationToken),
            commitOnError: false);
        Assert.IsType<ResourceRegistrationResourceGroupNotFound>(crossTenant.AssertFailure().Value);
    }

    [Fact]
    public async Task UpdateAndUnregister_AreGenericTransactionalAndIdempotent()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var register = Services.GetRequiredService<RegisterResource>();
        var update = Services.GetRequiredService<UpdateResourceRegistration>();
        var unregister = Services.GetRequiredService<UnregisterResource>();
        var descriptor = new ResourceRegistrationDescriptor(
            WebServiceType,
            "resource-1",
            "Before");
        await InTransactionAsync((session, cancellationToken) => register(
            group.TenantId,
            group.Id,
            descriptor,
            session,
            cancellationToken));

        using (var session = await DbContext.Client.StartSessionAsync())
        {
            session.StartTransaction();
            var updated = await update(
                group.TenantId,
                group.Id,
                descriptor with { DisplayName = "After" },
                session);
            Assert.Equal("After", updated.AssertSuccess().DisplayName);
            await session.CommitTransactionAsync();
        }

        foreach (var _ in Enumerable.Range(0, 2))
        {
            using var session = await DbContext.Client.StartSessionAsync();
            session.StartTransaction();
            (await unregister(
                group.TenantId,
                group.Id,
                WebServiceType,
                descriptor.ResourceId,
                session)).AssertSuccess();
            await session.CommitTransactionAsync();
        }

        Assert.False(await DbContext.ResourceRegistrations
            .Find(registration => registration.ResourceId == descriptor.ResourceId)
            .AnyAsync());
    }

    [Fact]
    public async Task ResourceGroupReads_ReturnBatchedCount_AndPaginatedRegistrations()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var register = Services.GetRequiredService<RegisterResource>();
        foreach (var resourceId in new[] { "resource-b", "resource-a" })
        {
            await InTransactionAsync((session, cancellationToken) => register(
                group.TenantId,
                group.Id,
                new ResourceRegistrationDescriptor(WebServiceType, resourceId, resourceId),
                session,
                cancellationToken));
        }

        var groups = (await Mediator.Send(new GetResourceGroupsRequest
        {
            TenantId = group.TenantId,
            PageNumber = 1,
            PageSize = 10
        })).AssertHandlerOutcome() switch
        {
            PaginatedResponse<Pole.Features.ResourceGroups.Application.Common.ResourceGroupDto>
                page => page
        };
        Assert.Equal(2, Assert.Single(groups.Items).ResourceCount);

        var resources = (await Mediator.Send(new GetResourceGroupResourcesRequest(
            group.Id,
            group.TenantId,
            PageNumber: 2,
            PageSize: 1))).AssertHandlerOutcome() switch
        {
            PaginatedResponse<Pole.Features.ResourceGroups.Application.Common.ResourceRegistrationDto>
                page => page,
            _ => throw new Xunit.Sdk.XunitException("Expected a resource registration page.")
        };
        Assert.Equal(2, resources.TotalCount);
        Assert.Equal("resource-b", Assert.Single(resources.Items).ResourceId);
    }

    [Fact]
    public async Task Migration_CreatesUniqueStableIdentityIndex()
    {
        await new ResourceRegistrationMigration().Up(Fixture.Database);
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var first = ResourceRegistrationFactory.Create(
            group.TenantId,
            group.Id,
            WebServiceType,
            "duplicate",
            "First",
            null,
            new ResourceRegistrationFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                DateTime.UtcNow));
        await DbContext.ResourceRegistrations.InsertOneAsync(first);

        var duplicate = first with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            DisplayName = "Second"
        };
        var exception = await Assert.ThrowsAsync<MongoWriteException>(() =>
            DbContext.ResourceRegistrations.InsertOneAsync(duplicate));
        Assert.Equal(ServerErrorCategory.DuplicateKey, exception.WriteError?.Category);
    }

    private async Task<Result<RegisteredResource, ResourceRegistrationMutationError>>
        InTransactionAsync(
            Func<IClientSessionHandle, CancellationToken,
                Task<Result<RegisteredResource, ResourceRegistrationMutationError>>> operation,
            bool commitOnError = true)
    {
        using var session = await DbContext.Client.StartSessionAsync();
        session.StartTransaction();
        var result = await operation(session, CancellationToken.None);
        if (result is Ok<RegisteredResource> || commitOnError)
            await session.CommitTransactionAsync();
        else
            await session.AbortTransactionAsync();
        return result;
    }
}
