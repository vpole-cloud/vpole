using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Events.Abstractions;
using Pole.Features.Realtime.Contracts;
using Pole.Features.RedisServices.Application;
using Pole.Features.RedisServices.Application.Realtime;
using Pole.Features.RedisServices.Events;
using Pole.Features.ResourceDeployments.Events;
using Xunit;

namespace Pole.Features.RedisServices.Tests;

public sealed class RedisRealtimeTests
{
    private const string Tenant = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Group = "bbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Resource = "redis-resource";

    [Theory]
    [InlineData("created")]
    [InlineData("updated")]
    [InlineData("deleted")]
    public async Task CrudHintsContainOnlyTenantResourceGroupAndRevision(string operation)
    {
        var hints = new List<RealtimeChange>();
        var consumer = Consumer(hints);
        var spec = new RedisServiceSpecSnapshot(new(7, "private-storage-profile"));
        var core = EventCore.Create(Tenant);
        await (operation switch
        {
            "created" => consumer.HandleAsync(
                new RedisServiceCreatedEvent(Resource, "private-name", Group, spec, ImmutableHashSet<string>.Empty, 6)
                {
                    EventCore = core
                }),
            "updated" => consumer.HandleAsync(new RedisServiceUpdatedEvent(Resource,
                    "private-name",
                    Group,
                    new(UpdateLog<RedisServiceSpecSnapshot>.Of(spec, spec),
                        UpdateLog<ImmutableHashSet<string>>.Of(ImmutableHashSet<string>.Empty,
                            ImmutableHashSet<string>.Empty)),
                    6)
            {
                EventCore = core
            }),
            "deleted" => consumer.HandleAsync(
                new RedisServiceDeletedEvent(Resource, "private-name", Group, spec, ImmutableHashSet<string>.Empty, 6)
                {
                    EventCore = core
                }),
            _ => throw new InvalidOperationException()
        });
        Assert.Equal(new RealtimeChange("redis", Tenant, Resource, Group, 6), Assert.Single(hints));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(hints));
    }

    [Theory]
    [InlineData("requested")]
    [InlineData("deletion-requested")]
    [InlineData("started")]
    [InlineData("applied")]
    [InlineData("failed")]
    [InlineData("deleted")]
    public async Task DeploymentHintsFilterProviderTypeAndSurviveDeletedResources(string operation)
    {
        var hints = new List<RealtimeChange>();
        var consumer = Consumer(hints);

        async Task Send(string type)
        {
            var core = EventCore.Create(Tenant);
            await (operation switch
            {
                "requested" => consumer.HandleAsync(
                    new ResourceDeploymentReconciliationRequestedEvent("deployment", "cell", 4, 6)
                    {
                        EventCore = core,
                        ResourceType = type,
                        ResourceId = Resource,
                        ResourceGroupId = Group
                    }),
                "deletion-requested" => consumer.HandleAsync(
                    new ResourceDeploymentDeletionRequestedEvent("deployment", "cell", 4, 6)
                    {
                        EventCore = core,
                        ResourceType = type,
                        ResourceId = Resource,
                        ResourceGroupId = Group
                    }),
                "started" => consumer.HandleAsync(
                    new ResourceDeploymentReconciliationStartedEvent("deployment",
                        type,
                        Resource,
                        Group,
                        "cell",
                        4,
                        "Apply",
                        6)
                    {
                        EventCore = core
                    }),
                "applied" => consumer.HandleAsync(
                    new ResourceDeploymentAppliedEvent("deployment", type, Resource, "cell", 4, 6)
                    {
                        EventCore = core,
                        ResourceGroupId = Group
                    }),
                "failed" => consumer.HandleAsync(
                    new ResourceDeploymentFailedEvent("deployment",
                        type,
                        Resource,
                        "cell",
                        4,
                        "Apply",
                        "private-failure",
                        true,
                        6)
                    {
                        EventCore = core,
                        ResourceGroupId = Group
                    }),
                "deleted" => consumer.HandleAsync(
                    new ResourceDeploymentDeletedEvent("deployment", type, Resource, "cell", 4, 6)
                    {
                        EventCore = core,
                        ResourceGroupId = Group
                    }),
                _ => throw new InvalidOperationException()
            });
        }

        await Send("pole/postgresql");
        Assert.Empty(hints);
        await Send("pole/redis");
        Assert.Equal(new RealtimeChange("redis", Tenant, Resource, Group, 6, 4), Assert.Single(hints));
        Assert.DoesNotContain("private-failure", JsonSerializer.Serialize(hints));
    }

    [Fact]
    public async Task HistoricalRequestsWithoutAdditiveIdentityInvalidateTenantReadsBroadly()
    {
        var hints = new List<RealtimeChange>();
        var payload =
            "{\"resourceDeploymentId\":\"deployment\",\"runtimeCellId\":\"cell\",\"generation\":4,\"resourceRevision\":6,\"eventCore\":{\"tenantId\":\"" +
            Tenant + "\"}}";
        await Consumer(hints)
            .HandleAsync(
                JsonSerializer.Deserialize<ResourceDeploymentReconciliationRequestedEvent>(payload,
                    EventExtensions.SerializerOptions)!);
        await Consumer(hints)
            .HandleAsync(
                JsonSerializer.Deserialize<ResourceDeploymentDeletionRequestedEvent>(payload,
                    EventExtensions.SerializerOptions)!);
        Assert.Equal(2, hints.Count);
        Assert.All(hints, hint => Assert.Equal(new RealtimeChange("redis", Tenant, Revision: 6, Generation: 4), hint));
        await Consumer(hints)
            .HandleAsync(new ResourceDeploymentDeletedEvent("deployment", "pole/redis", Resource, "cell", 4, 6)
            {
                EventCore = EventCore.Create(Tenant)
            });
        Assert.Equal(new RealtimeChange("redis", Tenant, Resource, Revision: 6, Generation: 4), hints[2]);
    }

    [Fact]
    public void RegistersItsTenantTopicOnce()
    {
        var services = new ServiceCollection();
        services.AddRedisServicesFeature();
        Assert.Equal(new RealtimeTopic("redis", RealtimeScope.Tenant),
            Assert.Single(services
                .Where(descriptor => descriptor.ServiceType == typeof(RealtimeTopic))
                .Select(descriptor => descriptor.ImplementationInstance)));
    }

    private static NotifyRedisChangedConsumer Consumer(List<RealtimeChange> hints) =>
        new((change, _) =>
        {
            hints.Add(change);
            return Task.CompletedTask;
        });
}
