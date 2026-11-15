using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Tenants.Application;
using Pole.Features.Tenants.Application.Members;
using Pole.Features.Tenants.Application.Realtime;
using Pole.Features.Tenants.Application.Tenants;
using Pole.Features.Tenants.Domain;
using Pole.Features.Users.Application;
using Microsoft.Extensions.Configuration;
using Pole.Features.Tenants.Events;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Tenants;

[Collection(IntegrationTestCollection.Global2)]
public sealed class TenantMembershipOutboxTests(
    MongoReplicaSetFixture fixture
) : IsolatedMediatorIntegrationTestBase(fixture, IntegrationSchemaProfile.Identity)
{
    private const string EventType = "pole.tenants/v1/membership-changed";

    [Fact]
    public async Task RoleUpdateChangesOnlyTheMatchedMemberOfTheImmutableList()
    {
        var tenant = CreateTenant();
        var first = TenantFactory.CreateMember(tenant.Id, "first", "first@example.test", TenantRole.Viewer);
        var selected = TenantFactory.CreateMember(tenant.Id, "selected", "selected@example.test", TenantRole.Member);
        tenant = tenant with { Members = tenant.Members.Add(first).Add(selected) };
        await Services.GetRequiredService<PersistCreatedTenant>()(tenant);
        var eventsBefore = await DbContext.CountAllOutboxMessagesAsync(EventType);

        var result = await Services.GetRequiredService<PersistUpdatedTenantMemberRole>()(
            new UpdateTenantMemberRole(
                tenant.Id,
                selected.Id,
                TenantRole.Admin,
                new TenantMemberActor("test-admin", true)));

        Assert.Equal(TenantRole.Admin, Assert.IsType<TenantMember>(result.Value).Role);
        var stored = await DbContext.Tenants.Find(item => item.Id == tenant.Id).SingleAsync();
        Assert.Equal(TenantRole.Owner, stored.Members[0].Role);
        Assert.Equal(TenantRole.Viewer, stored.Members[1].Role);
        Assert.Equal(selected.Id, stored.Members[2].Id);
        Assert.Equal(TenantRole.Admin, stored.Members[2].Role);
        Assert.Equal(eventsBefore + 1, await DbContext.CountAllOutboxMessagesAsync(EventType));
    }

    [Fact]
    public async Task AcceptedMembershipChangesEmitSubjectHintsAndNoOpsDoNot()
    {
        var tenant = CreateTenant();
        await Services.GetRequiredService<PersistCreatedTenant>()(tenant);
        var member = TenantFactory.CreateMember(tenant.Id, "new-subject", "private@example.test", TenantRole.Member);
        // Mutation-specific invitation/acceptance atomicity is covered by TenantInvitationTransactionTests.
        // This fixture already has a member so this test isolates role/removal outbox behavior.
        await DbContext.Tenants.ReplaceOneAsync(
            item => item.Id == tenant.Id,
            tenant with { Members = tenant.Members.Add(member) });
        var update = Services.GetRequiredService<PersistUpdatedTenantMemberRole>();
        await update(
            new UpdateTenantMemberRole(
                tenant.Id,
                member.Id,
                TenantRole.Viewer,
                new TenantMemberActor("test-admin", true)));
        await update(
            new UpdateTenantMemberRole(
                tenant.Id,
                member.Id,
                TenantRole.Viewer,
                new TenantMemberActor("test-admin", true)));
        await Services.GetRequiredService<PersistRemovedTenantMember>()(
            new RemoveTenantMember(tenant.Id, member.Id, new TenantMemberActor("test-admin", true)));
        var records = await DbContext.OutboxMessages.Find(message => message.EventType == EventType).ToListAsync();
        Assert.Equal(3, records.Count);
        Assert.All(
            records,
            record =>
            {
                Assert.Equal(tenant.Id, record.TenantId);
                Assert.DoesNotContain("private@example.test", record.PayloadJson);
                Assert.DoesNotContain("Viewer", record.PayloadJson);
                var payload = JsonSerializer.Deserialize<TenantMembershipChangedEvent>(
                    record.PayloadJson,
                    EventExtensions.SerializerOptions)!;
                Assert.Equal(tenant.Id, payload.EventCore.TenantId);
                Assert.Contains(payload.UserId, new[] { "owner-subject", "new-subject" });
            });
    }

    [Fact]
    public async Task DefaultTenantRetryDoesNotEmitDuplicateMembershipHints()
    {
        var tenant = CreateTenant();
        var ensure = Services.GetRequiredService<EnsureDefaultTenant>();
        await ensure(tenant);
        Assert.Equal(1, await DbContext.CountAllOutboxMessagesAsync(EventType));
        Assert.Equal(tenant.Id, (await DbContext.Tenants.Find(item => item.Id == tenant.Id).SingleAsync()).Id);
        await ensure(tenant);
        Assert.Equal(1, await DbContext.CountAllOutboxMessagesAsync(EventType));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("remove")]
    public async Task OutboxFailureRollsBackEveryMembershipMutation(string operation)
    {
        var tenant = CreateTenant();
        var member = TenantFactory.CreateMember(tenant.Id, "new-subject", "private@example.test", TenantRole.Member);
        if (operation != "create")
        {
            tenant = tenant with
            {
                Members = tenant.Members.Add(member),
            };
            await DbContext.Tenants.InsertOneAsync(tenant);
        }

        var before = await DbContext.Tenants.Find(candidate => candidate.Id == tenant.Id).SingleOrDefaultAsync();
        var services = new ServiceCollection();
        services.AddSingleton(Fixture.Database);
        services.AddSingleton(Fixture.Client);
        services.AddSingleton<MongoRunTransaction>();
        services.AddSingleton<StoreOutboxWrites>(async (
            session, writes,
            ct) =>
        {
            await Fixture.StoreOutboxWrites(session, writes, ct);
            throw new InvalidOperationException("outbox unavailable");
        });
        services.AddSingleton(TimeProvider.System);
        services.AddUsersFeature(new ConfigurationBuilder().Build());
        services.AddTenantsFeature();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        Func<Task> mutate = operation switch
        {
            "create" => () => scope.ServiceProvider.GetRequiredService<PersistCreatedTenant>()(tenant),
            "update" => async () =>
            {
                await scope.ServiceProvider.GetRequiredService<PersistUpdatedTenantMemberRole>()(
                    new UpdateTenantMemberRole(
                        tenant.Id,
                        member.Id,
                        TenantRole.Viewer,
                        new TenantMemberActor("test-admin", true)));
            },
            "remove" => async () =>
            {
                await scope.ServiceProvider.GetRequiredService<PersistRemovedTenantMember>()(
                    new RemoveTenantMember(tenant.Id, member.Id, new TenantMemberActor("test-admin", true)));
            },
            _ => throw new ArgumentException(nameof(operation)),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(mutate);
        var after = await DbContext.Tenants.Find(candidate => candidate.Id == tenant.Id).SingleOrDefaultAsync();
        if (before is null)
            Assert.Null(after);
        else
            Assert.Equal(before.Members.ToArray(), after!.Members.ToArray());
        Assert.Equal(0, await DbContext.CountAllOutboxMessagesAsync(EventType));
    }

    [Fact]
    public async Task ConcurrentOwnerDowngradesPreserveTheLastOwnerAndEmitOnlyTheAcceptedChange()
    {
        var tenant = CreateTenant();
        var second = TenantFactory.CreateMember(tenant.Id, "second-owner", "second@example.test", TenantRole.Owner);
        tenant = tenant with
        {
            Members = tenant.Members.Add(second),
        };
        await DbContext.Tenants.InsertOneAsync(tenant);
        using var firstScope = Services.CreateScope();
        using var secondScope = Services.CreateScope();
        var outcomes = await Task.WhenAll(
            firstScope.ServiceProvider.GetRequiredService<PersistUpdatedTenantMemberRole>()(
                new UpdateTenantMemberRole(
                    tenant.Id,
                    tenant.Members[0].Id,
                    TenantRole.Viewer,
                    new TenantMemberActor("test-admin", true))),
            secondScope.ServiceProvider.GetRequiredService<PersistUpdatedTenantMemberRole>()(
                new UpdateTenantMemberRole(
                    tenant.Id,
                    second.Id,
                    TenantRole.Viewer,
                    new TenantMemberActor("test-admin", true))));
        Assert.Single(outcomes, outcome => outcome.Value is TenantMember);
        Assert.Single(outcomes, outcome => outcome.Value is TenantLastOwner);
        Assert.Single(
            (await DbContext.Tenants.Find(candidate => candidate.Id == tenant.Id).SingleAsync()).Members,
            value => value.Role == TenantRole.Owner);
        Assert.Equal(1, await DbContext.CountAllOutboxMessagesAsync(EventType));
    }

    [Fact]
    public async Task CommittedMembershipConsumerPublishesOnlyOwnSubjectDiscoveryHint()
    {
        var notices = new List<RealtimeChange>();
        var consumer = new NotifyTenantAccessChangedConsumer((change, _) =>
        {
            notices.Add(change);
            return Task.CompletedTask;
        });
        await consumer.HandleAsync(
            new TenantMembershipChangedEvent("owner-subject")
            {
                EventCore = EventCore.Create(Data.TenantId),
            });
        Assert.Equal(new RealtimeChange("tenant-access", ResourceId: "owner-subject"), notices[0]);
        Assert.Equal(new RealtimeChange("tenant-members", Data.TenantId), notices[1]);
        Assert.Equal(2, notices.Count);
    }

    private static Tenant CreateTenant()
    {
        var tenant = TenantFactory.Create("Membership test");
        return tenant with
        {
            Members =
            [
                TenantFactory.CreateMember(tenant.Id, "owner-subject", "owner@example.test", TenantRole.Owner),
            ],
        };
    }
}
