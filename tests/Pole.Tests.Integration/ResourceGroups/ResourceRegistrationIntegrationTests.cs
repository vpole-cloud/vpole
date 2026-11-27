// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Request;
using Pole.Features.ResourceGroups.Application;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Migrations;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Sdk;

namespace Pole.Tests.Integration.ResourceGroups;

[Collection(IntegrationTestCollection.Tenant3)]
public sealed class ResourceRegistrationIntegrationTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    private static readonly ResourceTypeId WebServiceType = ResourceTypeId.Create("pole/web-service");

    [Fact]
    public async Task Register_IsIdempotent_AndRejectsCrossGroupIdentityReuse()
    {
        var firstGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("first"));
        var secondGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("second"));
        var registrar = Services.GetRequiredService<IResourceRegistrar>();
        var descriptor = new ResourceRegistrationDescriptor(WebServiceType, "resource-1", "Web API");

        var first = await InTransactionAsync((session, cancellationToken) => registrar.RegisterAsync(
            new RegisterResourceRequest(
                TenantId: firstGroup.TenantId,
                ResourceGroupId: firstGroup.Id,
                Resource: descriptor),
            session: session,
            cancellationToken: cancellationToken));
        var repeated = await InTransactionAsync((session, cancellationToken) => registrar.RegisterAsync(
            new RegisterResourceRequest(
                TenantId: firstGroup.TenantId,
                ResourceGroupId: firstGroup.Id,
                Resource: descriptor),
            session: session,
            cancellationToken: cancellationToken));
        var conflict = await InTransactionAsync(
            (session, cancellationToken) => registrar.RegisterAsync(
                new RegisterResourceRequest(
                    TenantId: secondGroup.TenantId,
                    ResourceGroupId: secondGroup.Id,
                    Resource: descriptor),
                session: session,
                cancellationToken: cancellationToken),
            commitOnError: false);

        Assert.Equal(
            Assert.IsType<RegisteredResource>(first.Value).RegistrationId,
            Assert.IsType<RegisteredResource>(repeated.Value).RegistrationId);
        Assert.IsType<ResourceRegistrationIdentityConflict>(
            Assert.IsType<ResourceRegistrationMutationError>(conflict.Value).Value);
        Assert.Equal(
            1L,
            await DbContext.ResourceRegistrations.CountDocumentsAsync(
                Builders<ResourceRegistration>.Filter.Eq(item => item.TenantId, Data.TenantId)));
    }

    [Fact]
    public async Task Register_RollsBackWithOwningTransaction_AndIsTenantScoped()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var registrar = Services.GetRequiredService<IResourceRegistrar>();
        using (var session = await DbContext.Client.StartSessionAsync())
        {
            session.StartTransaction();
            Assert.IsType<RegisteredResource>(
                (await registrar.RegisterAsync(
                    new RegisterResourceRequest(
                        TenantId: group.TenantId,
                        ResourceGroupId: group.Id,
                        Resource: new ResourceRegistrationDescriptor(WebServiceType, "rolled-back", "Rolled back")),
                    session: session)).Value);
            await session.AbortTransactionAsync();
        }

        Assert.False(
            await DbContext.ResourceRegistrations
                .Find(registration =>
                    registration.TenantId == Data.TenantId && registration.ResourceId == "rolled-back")
                .AnyAsync());
        var unchangedGroup = await DbContext.ResourceGroups.Find(candidate => candidate.Id == group.Id).SingleAsync();
        Assert.Equal(group.RegistrationFence, unchangedGroup.RegistrationFence);

        using var inactiveSession = await DbContext.Client.StartSessionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => registrar.RegisterAsync(
            new RegisterResourceRequest(
                TenantId: group.TenantId,
                ResourceGroupId: group.Id,
                Resource: new ResourceRegistrationDescriptor(WebServiceType, "invalid", "Invalid")),
            session: inactiveSession));

        var crossTenant = await InTransactionAsync(
            (session, cancellationToken) => registrar.RegisterAsync(
                new RegisterResourceRequest(
                    TenantId: ObjectId.GenerateNewId().ToString(),
                    ResourceGroupId: group.Id,
                    Resource: new ResourceRegistrationDescriptor(WebServiceType, "cross-tenant", "Invalid")),
                session: session,
                cancellationToken: cancellationToken),
            commitOnError: false);
        Assert.IsType<ResourceRegistrationResourceGroupNotFound>(
            Assert.IsType<ResourceRegistrationMutationError>(crossTenant.Value).Value);
    }

    [Fact]
    public async Task Register_AllowsTheSameStableIdentityInDifferentTenants()
    {
        var otherTenantId = ObjectId.GenerateNewId().ToString();
        var firstGroup = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("first-tenant"));
        var secondGroup = await DbContext.InsertResourceGroupAsync(
            Data.SeedResourceGroup("second-tenant", otherTenantId));
        var registrar = Services.GetRequiredService<IResourceRegistrar>();
        var descriptor = new ResourceRegistrationDescriptor(WebServiceType, "shared-resource-id", "Shared identity");

        var first = await InTransactionAsync((session, cancellationToken) => registrar.RegisterAsync(
            new RegisterResourceRequest(
                TenantId: firstGroup.TenantId,
                ResourceGroupId: firstGroup.Id,
                Resource: descriptor),
            session: session,
            cancellationToken: cancellationToken));
        var second = await InTransactionAsync((session, cancellationToken) => registrar.RegisterAsync(
            new RegisterResourceRequest(
                TenantId: secondGroup.TenantId,
                ResourceGroupId: secondGroup.Id,
                Resource: descriptor),
            session: session,
            cancellationToken: cancellationToken));

        Assert.NotEqual(
            Assert.IsType<RegisteredResource>(first.Value).RegistrationId,
            Assert.IsType<RegisteredResource>(second.Value).RegistrationId);
        var registrations = await DbContext.ResourceRegistrations
            .Find(registration =>
                (registration.TenantId == firstGroup.TenantId || registration.TenantId == secondGroup.TenantId) &&
                registration.ResourceId == descriptor.ResourceId)
            .ToListAsync();
        Assert.Equal(2, registrations.Count);
        Assert.Equal(
            new[] { firstGroup.TenantId, secondGroup.TenantId }.Order(),
            registrations.Select(registration => registration.TenantId).Order());
    }

    [Fact]
    public async Task BoundRegistrar_EnforcesPlatformAndPluginNamespacesForEveryMutation()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var platformRegistrar = Services.GetRequiredService<IResourceRegistrar>();
        var pluginRegistrar = Services.CreatePluginResourceRegistrar(" acme ");
        Assert.Throws<ArgumentException>(() => Services.CreatePluginResourceRegistrar("pole"));
        var descriptor = new ResourceRegistrationDescriptor(
            ResourceTypeId.Create("acme/cache"),
            "plugin-resource",
            "Plugin cache");

        await Assert.ThrowsAsync<ArgumentException>(() => InTransactionAsync((session, cancellationToken) =>
            platformRegistrar.RegisterAsync(
                new RegisterResourceRequest(TenantId: group.TenantId, ResourceGroupId: group.Id, Resource: descriptor),
                session: session,
                cancellationToken: cancellationToken)));

        var registered = await InTransactionAsync((session, cancellationToken) => pluginRegistrar.RegisterAsync(
            new RegisterResourceRequest(TenantId: group.TenantId, ResourceGroupId: group.Id, Resource: descriptor),
            session: session,
            cancellationToken: cancellationToken));
        Assert.Equal("acme", Assert.IsType<RegisteredResource>(registered.Value).OwningPluginId);

        var updateConflict = await InTransactionAsync(
            (session, cancellationToken) => platformRegistrar.UpdateAsync(
                new UpdateRegisteredResourceRequest(
                    TenantId: group.TenantId,
                    ResourceGroupId: group.Id,
                    Resource: descriptor with { DisplayName = "Platform takeover" }),
                session: session,
                cancellationToken: cancellationToken),
            commitOnError: false);
        Assert.IsType<ResourceRegistrationIdentityConflict>(
            Assert.IsType<ResourceRegistrationMutationError>(updateConflict.Value).Value);

        using (var session = await DbContext.Client.StartSessionAsync())
        {
            session.StartTransaction();
            var unregisterConflict = await platformRegistrar.UnregisterAsync(
                new UnregisterResourceRequest(
                    TenantId: group.TenantId,
                    ResourceGroupId: group.Id,
                    ResourceTypeId: descriptor.ResourceTypeId,
                    ResourceId: descriptor.ResourceId),
                session: session);
            Assert.IsType<ResourceRegistrationIdentityConflict>(
                Assert.IsType<ResourceRegistrationMutationError>(unregisterConflict.Value).Value);
            await session.AbortTransactionAsync();
        }

        await Assert.ThrowsAsync<ArgumentException>(() => InTransactionAsync((session, cancellationToken) =>
            pluginRegistrar.RegisterAsync(
                new RegisterResourceRequest(
                    TenantId: group.TenantId,
                    ResourceGroupId: group.Id,
                    Resource: new ResourceRegistrationDescriptor(
                        ResourceTypeId.Create("other/cache"),
                        "other-resource",
                        "Other cache")),
                session: session,
                cancellationToken: cancellationToken)));

        using (var session = await DbContext.Client.StartSessionAsync())
        {
            session.StartTransaction();
            Assert.IsType<ResourceUnregistered>(
                (await pluginRegistrar.UnregisterAsync(
                    new UnregisterResourceRequest(
                        TenantId: group.TenantId,
                        ResourceGroupId: group.Id,
                        ResourceTypeId: descriptor.ResourceTypeId,
                        ResourceId: descriptor.ResourceId),
                    session: session)).Value);
            await session.CommitTransactionAsync();
        }

        Assert.False(
            await DbContext.ResourceRegistrations
                .Find(registration =>
                    registration.TenantId == Data.TenantId && registration.ResourceId == descriptor.ResourceId)
                .AnyAsync());
    }

    [Fact]
    public async Task UpdateAndUnregister_AreGenericTransactionalAndIdempotent()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var registrar = Services.GetRequiredService<IResourceRegistrar>();
        var descriptor = new ResourceRegistrationDescriptor(WebServiceType, "resource-1", "Before");
        await InTransactionAsync((session, cancellationToken) => registrar.RegisterAsync(
            new RegisterResourceRequest(TenantId: group.TenantId, ResourceGroupId: group.Id, Resource: descriptor),
            session: session,
            cancellationToken: cancellationToken));

        RegisteredResource firstUpdate;
        using (var session = await DbContext.Client.StartSessionAsync())
        {
            session.StartTransaction();
            var updated = await registrar.UpdateAsync(
                new UpdateRegisteredResourceRequest(
                    TenantId: group.TenantId,
                    ResourceGroupId: group.Id,
                    Resource: descriptor with { DisplayName = "After" }),
                session: session);
            firstUpdate = Assert.IsType<RegisteredResource>(updated.Value);
            Assert.Equal("After", firstUpdate.DisplayName);
            await session.CommitTransactionAsync();
        }

        var groupAfterFirstUpdate = await DbContext.ResourceGroups
            .Find(candidate => candidate.Id == group.Id)
            .SingleAsync();
        var registrationAfterFirstUpdate = await DbContext.ResourceRegistrations
            .Find(candidate => candidate.TenantId == Data.TenantId && candidate.ResourceId == descriptor.ResourceId)
            .SingleAsync();
        using (var session = await DbContext.Client.StartSessionAsync())
        {
            session.StartTransaction();
            var repeatedUpdate = await registrar.UpdateAsync(
                new UpdateRegisteredResourceRequest(
                    TenantId: group.TenantId,
                    ResourceGroupId: group.Id,
                    Resource: descriptor with { DisplayName = "After" }),
                session: session);
            Assert.Equal(
                registrationAfterFirstUpdate.UpdatedAtUtc,
                Assert.IsType<RegisteredResource>(repeatedUpdate.Value).UpdatedAtUtc);
            await session.CommitTransactionAsync();
        }

        var groupAfterRepeatedUpdate = await DbContext.ResourceGroups
            .Find(candidate => candidate.Id == group.Id)
            .SingleAsync();
        Assert.Equal(groupAfterFirstUpdate.RegistrationFence, groupAfterRepeatedUpdate.RegistrationFence);
        var registrationAfterRepeatedUpdate = await DbContext.ResourceRegistrations
            .Find(candidate => candidate.TenantId == Data.TenantId && candidate.ResourceId == descriptor.ResourceId)
            .SingleAsync();
        Assert.Equal(registrationAfterFirstUpdate.UpdatedAtUtc, registrationAfterRepeatedUpdate.UpdatedAtUtc);

        foreach (var _ in Enumerable.Range(0, 2))
        {
            using var session = await DbContext.Client.StartSessionAsync();
            session.StartTransaction();
            Assert.IsType<ResourceUnregistered>(
                (await registrar.UnregisterAsync(
                    new UnregisterResourceRequest(
                        TenantId: group.TenantId,
                        ResourceGroupId: group.Id,
                        ResourceTypeId: WebServiceType,
                        ResourceId: descriptor.ResourceId),
                    session: session)).Value);
            await session.CommitTransactionAsync();
        }

        Assert.False(
            await DbContext.ResourceRegistrations
                .Find(registration =>
                    registration.TenantId == Data.TenantId && registration.ResourceId == descriptor.ResourceId)
                .AnyAsync());
    }

    [Fact]
    public async Task ResourceGroupReads_ReturnBatchedCount_AndPaginatedRegistrations()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var registrar = Services.GetRequiredService<IResourceRegistrar>();
        foreach (var resourceId in new[] { "resource-b", "resource-a" })
            await InTransactionAsync((session, cancellationToken) => registrar.RegisterAsync(
                new RegisterResourceRequest(
                    TenantId: group.TenantId,
                    ResourceGroupId: group.Id,
                    Resource: new ResourceRegistrationDescriptor(WebServiceType, resourceId, resourceId)),
                session: session,
                cancellationToken: cancellationToken));

        var groups = (await Mediator.Send(
                new GetResourceGroupsRequest
                {
                    TenantId = group.TenantId,
                    PageNumber = 1,
                    PageSize = 10,
                })).AssertHandlerOutcome() switch
            {
                PaginatedResponse<ResourceGroupDto> page => page,
            };
        Assert.Equal(2, Assert.Single(groups.Items).ResourceCount);

        var resources
            = (await Mediator.Send(
                    new GetResourceGroupResourcesRequest(group.Id, group.TenantId, PageNumber: 2, PageSize: 1)))
                .AssertHandlerOutcome() switch
                {
                    PaginatedResponse<ResourceRegistrationDto> page => page,
                    _ => throw new XunitException("Expected a resource registration page."),
                };
        Assert.Equal(2, resources.TotalCount);
        Assert.Equal("resource-b", Assert.Single(resources.Items).ResourceId);
    }


    private async Task<ResourceRegistrationOutcome> InTransactionAsync(
        Func<IClientSessionHandle, CancellationToken, Task<ResourceRegistrationOutcome>> operation,
        bool commitOnError = true)
    {
        using var session = await DbContext.Client.StartSessionAsync();
        session.StartTransaction();
        var result = await operation(session, CancellationToken.None);
        if (result is RegisteredResource || commitOnError)
            await session.CommitTransactionAsync();
        else
            await session.AbortTransactionAsync();
        return result;
    }
}

[Collection(IntegrationTestCollection.Global4)]
public sealed class ResourceRegistrationMigrationTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Migration_CreatesUniqueStableIdentityIndex()
    {
        await new ResourceRegistrationMigration().Up(Fixture.Database);
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var first = ResourceRegistrationFactory.Create(
            group.TenantId,
            group.Id,
            ResourceTypeId.Create("pole/web-service"),
            "duplicate",
            "First",
            null,
            new ResourceRegistrationFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow));
        await DbContext.ResourceRegistrations.InsertOneAsync(first);

        var duplicate = first with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            DisplayName = "Second",
        };
        var exception = await Assert.ThrowsAsync<MongoWriteException>(() =>
            DbContext.ResourceRegistrations.InsertOneAsync(duplicate));
        Assert.Equal(ServerErrorCategory.DuplicateKey, exception.WriteError?.Category);
    }
}
