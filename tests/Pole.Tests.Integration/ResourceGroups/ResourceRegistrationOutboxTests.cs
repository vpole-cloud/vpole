using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceRegistrations;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Events;
using Pole.Observability;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.ResourceGroups;

public sealed class ResourceRegistrationOutboxTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private const string Registered = "pole.resource-groups/v1/resource-registered";
    private const string Updated = "pole.resource-groups/v1/resource-registration-updated";

    private static readonly ResourceRegistrationDescriptor Descriptor = new(ResourceTypeId.Create("pole/test-resource"),
        "logical-resource",
        "Before");

    [Fact]
    public async Task AcceptedRegistrationAndDisplayNameChangeWriteTenantEventsExactlyOnce()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var registrar = Services.GetRequiredService<IResourceRegistrar>();
        using var trace = TraceContext.Push(new("0123456789abcdef0123456789abcdef", "registration-realtime-test"));
        using (var session = await DbContext.Client.StartSessionAsync())
        {
            session.StartTransaction();
            Assert.IsType<RegisteredResource>(
                (await registrar.RegisterAsync(
                    new RegisterResourceRequest(
                        TenantId: group.TenantId,
                        ResourceGroupId: group.Id,
                        Resource: Descriptor),
                    session: session)).Value);
            // The event and registration are both invisible outside the uncommitted transaction.
            Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(Registered));
            await session.CommitTransactionAsync();
        }

        using (var session = await DbContext.Client.StartSessionAsync())
        {
            session.StartTransaction();
            Assert.IsType<RegisteredResource>(
                (await registrar.RegisterAsync(
                    new RegisterResourceRequest(
                        TenantId: group.TenantId,
                        ResourceGroupId: group.Id,
                        Resource: Descriptor),
                    session: session)).Value);
            Assert.IsType<RegisteredResource>(
                (await registrar.UpdateAsync(
                    new UpdateRegisteredResourceRequest(
                        TenantId: group.TenantId,
                        ResourceGroupId: group.Id,
                        Resource: Descriptor),
                    session: session)).Value);
            Assert.IsType<RegisteredResource>((await registrar.UpdateAsync(
                new UpdateRegisteredResourceRequest(
                    TenantId: group.TenantId,
                    ResourceGroupId: group.Id,
                    Resource: Descriptor with
                    {
                        DisplayName = "After"
                    }),
                session: session)).Value);
            Assert.IsType<RegisteredResource>((await registrar.UpdateAsync(
                new UpdateRegisteredResourceRequest(
                    TenantId: group.TenantId,
                    ResourceGroupId: group.Id,
                    Resource: Descriptor with
                    {
                        DisplayName = "After"
                    }),
                session: session)).Value);
            await session.CommitTransactionAsync();
        }

        var registered = await DbContext.OutboxMessages.Find(message => message.EventType == Registered).SingleAsync();
        var updated = await DbContext.OutboxMessages.Find(message => message.EventType == Updated).SingleAsync();
        Assert.Equal(group.TenantId, registered.TenantId);
        Assert.Equal(group.TenantId, updated.TenantId);
        Assert.Equal("0123456789abcdef0123456789abcdef", updated.TraceId);
        Assert.Equal("registration-realtime-test", updated.CorrelationId);
        var payload =
            JsonSerializer.Deserialize<ResourceRegistrationUpdatedEvent>(updated.PayloadJson,
                EventExtensions.SerializerOptions)!;
        Assert.Equal(group.TenantId, payload.EventCore.TenantId);
        Assert.Equal(group.Id, payload.ResourceGroupId);
        Assert.Equal(Descriptor.ResourceId, payload.ResourceId);
        Assert.Equal(Descriptor.ResourceTypeId.Value, payload.ResourceType);
        Assert.DoesNotContain("After", updated.PayloadJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutboxFailureRollsBackRegistrationOrDisplayNameMutation(bool updating)
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        if (updating)
        {
            using var seed = await DbContext.Client.StartSessionAsync();
            seed.StartTransaction();
            await Services.GetRequiredService<IResourceRegistrar>()
                .RegisterAsync(
                new RegisterResourceRequest(
                    TenantId: group.TenantId,
                    ResourceGroupId: group.Id,
                    Resource: Descriptor),
                session: seed);
            await seed.CommitTransactionAsync();
        }

        var before = await DbContext.ResourceGroups.Find(candidate => candidate.Id == group.Id).SingleAsync();
        var outboxCount = await DbContext.OutboxMessages.CountDocumentsAsync(_ => true);
        var realStore = Services.GetRequiredService<StoreOutboxWrites>();
        var mutations = new MongoResourceRegistrationMutations(DbContext.ResourceGroups,
            DbContext.ResourceRegistrations,
            TimeProvider.System,
            async (session, writes, ct) =>
            {
                await realStore(session, writes, ct);
                throw new InvalidOperationException("outbox unavailable");
            });
        using (var session = await DbContext.Client.StartSessionAsync())
        {
            session.StartTransaction();
            await Assert.ThrowsAsync<InvalidOperationException>(() => updating
                ? mutations.UpdateAsync(null,
                    group.TenantId,
                    group.Id,
                    Descriptor with
                    {
                        DisplayName = "After"
                    },
                    session)
                : mutations.RegisterAsync(null, group.TenantId, group.Id, Descriptor, session));
            await session.AbortTransactionAsync();
        }

        Assert.Equal(outboxCount, await DbContext.OutboxMessages.CountDocumentsAsync(_ => true));
        Assert.Equal(before.RegistrationFence,
            (await DbContext.ResourceGroups.Find(candidate => candidate.Id == group.Id).SingleAsync())
            .RegistrationFence);
        var registration = await DbContext.ResourceRegistrations
            .Find(candidate => candidate.ResourceId == Descriptor.ResourceId).SingleOrDefaultAsync();
        if (updating)
            Assert.Equal("Before", registration!.DisplayName);
        else
            Assert.Null(registration);
    }

    [Fact]
    public async Task TypedConflictsAndMissingUpdatesDoNotWriteRegistryEvents()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("first"));
        var other = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("second"));
        var registrar = Services.GetRequiredService<IResourceRegistrar>();
        using (var session = await DbContext.Client.StartSessionAsync())
        {
            session.StartTransaction();
            await registrar.RegisterAsync(
                new RegisterResourceRequest(
                    TenantId: group.TenantId,
                    ResourceGroupId: group.Id,
                    Resource: Descriptor),
                session: session);
            await session.CommitTransactionAsync();
        }

        using (var session = await DbContext.Client.StartSessionAsync())
        {
            session.StartTransaction();
            Assert.IsType<ResourceRegistrationMutationError>(
                (await registrar.RegisterAsync(
                    new RegisterResourceRequest(
                        TenantId: other.TenantId,
                        ResourceGroupId: other.Id,
                        Resource: Descriptor),
                    session: session)).Value);
            Assert.IsType<ResourceRegistrationMutationError>(
                (await registrar.UpdateAsync(
                    new UpdateRegisteredResourceRequest(
                        TenantId: other.TenantId,
                        ResourceGroupId: other.Id,
                        Resource: Descriptor),
                    session: session)).Value);
            Assert.IsType<ResourceRegistrationMutationError>((await registrar.UpdateAsync(
                new UpdateRegisteredResourceRequest(
                    TenantId: group.TenantId,
                    ResourceGroupId: group.Id,
                    Resource: Descriptor with
                    {
                        ResourceId = "missing"
                    }),
                session: session)).Value);
            await session.CommitTransactionAsync();
        }

        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(Registered));
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(Updated));
    }
}
