using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Events.Abstractions;
using Pole.Features.Realtime.Contracts;
using Pole.Features.ResourceGroups.Application;
using Pole.Features.ResourceGroups.Application.Realtime;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.ResourceDeployments.Application.Realtime;
using Pole.Features.ResourceDeployments.Events;
using Xunit;

namespace Pole.Tests.Integration.Realtime;

public sealed class ResourceProviderRealtimeTests
{
    [Fact]
    public async Task ScopeAndCellLifecycleRefreshMetadataWithoutPublishingNativeFailureDetails()
    {
        var notices = new List<RealtimeChange>();
        var consumer = new NotifyResourceGroupChangedConsumer((change, _) =>
        {
            notices.Add(change);
            return Task.CompletedTask;
        });
        var core = EventCore.Create("000000000000000000000001");
        await consumer.HandleAsync(new IsolationScopeCreatedEvent("scope", "group", "private-name", false)
        {
            EventCore = core
        });
        Assert.All(notices, notice => Assert.Equal("group", notice.ResourceGroupId));
        notices.Clear();
        await consumer.HandleAsync(new RuntimeCellReconciliationFailedEvent("cell", 7, "private-failure-detail")
        {
            EventCore = core
        });
        Assert.Equal(["resource-groups", "workspace-inventory"], notices.Select(notice => notice.Topic));
        Assert.All(notices,
            notice =>
            {
                Assert.Equal(core.TenantId, notice.TenantId);
                Assert.Equal(7, notice.Generation);
                Assert.Null(notice.ResourceId);
                Assert.Null(notice.ResourceGroupId);
            });
    }

    private const string Tenant = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Group = "bbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Resource = "logical-resource";

    [Theory]
    [InlineData("requested")]
    [InlineData("deletion-requested")]
    [InlineData("started")]
    [InlineData("applied")]
    [InlineData("failed")]
    [InlineData("deleted")]
    public async Task CommittedDeploymentMetadataHintsUseLogicalInventoryIdentityWithoutFailurePayloads(
        string operation)
    {
        var hints = new List<RealtimeChange>();
        var consumer = new NotifyResourceDeploymentInventoryChangedConsumer((change, _) =>
        {
            hints.Add(change);
            return Task.CompletedTask;
        });
        var core = EventCore.Create(Tenant);
        await (operation switch
        {
            "requested" => consumer.HandleAsync(
                new ResourceDeploymentReconciliationRequestedEvent("deployment", "cell", 4, 6)
                {
                    EventCore = core,
                    ResourceType = "acme/custom",
                    ResourceId = Resource,
                    ResourceGroupId = Group
                }),
            "deletion-requested" => consumer.HandleAsync(
                new ResourceDeploymentDeletionRequestedEvent("deployment", "cell", 4, 6)
                {
                    EventCore = core,
                    ResourceType = "acme/custom",
                    ResourceId = Resource,
                    ResourceGroupId = Group
                }),
            "started" => consumer.HandleAsync(
                new ResourceDeploymentReconciliationStartedEvent("deployment",
                    "acme/custom",
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
                new ResourceDeploymentAppliedEvent("deployment", "acme/custom", Resource, "cell", 4, 6)
                {
                    EventCore = core,
                    ResourceGroupId = Group
                }),
            "failed" => consumer.HandleAsync(
                new ResourceDeploymentFailedEvent("deployment",
                    "acme/custom",
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
                new ResourceDeploymentDeletedEvent("deployment", "acme/custom", Resource, "cell", 4, 6)
                {
                    EventCore = core,
                    ResourceGroupId = Group
                }),
            _ => throw new InvalidOperationException()
        });
        Assert.Equal(new RealtimeChange("workspace-inventory", Tenant, Resource, Group, 6, 4), Assert.Single(hints));
        Assert.DoesNotContain("private-failure", JsonSerializer.Serialize(hints));
        Assert.DoesNotContain("deployment", JsonSerializer.Serialize(hints));
    }

    [Fact]
    public async Task HistoricalDeploymentRequestsAndMissingGroupIdentityRemainSafeInventoryHints()
    {
        var hints = new List<RealtimeChange>();
        var consumer = new NotifyResourceDeploymentInventoryChangedConsumer((change, _) =>
        {
            hints.Add(change);
            return Task.CompletedTask;
        });
        var payload =
            "{\"resourceDeploymentId\":\"deployment\",\"runtimeCellId\":\"cell\",\"generation\":4,\"resourceRevision\":6,\"eventCore\":{\"tenantId\":\"" +
            Tenant + "\"}}";
        await consumer.HandleAsync(
            JsonSerializer.Deserialize<ResourceDeploymentReconciliationRequestedEvent>(payload,
                EventExtensions.SerializerOptions)!);
        await consumer.HandleAsync(
            JsonSerializer.Deserialize<ResourceDeploymentDeletionRequestedEvent>(payload,
                EventExtensions.SerializerOptions)!);
        Assert.All(hints,
            hint => Assert.Equal(new RealtimeChange("workspace-inventory", Tenant, Revision: 6, Generation: 4), hint));
        await consumer.HandleAsync(
            new ResourceDeploymentDeletedEvent("deployment", "acme/custom", Resource, "cell", 4, 6)
            {
                EventCore = EventCore.Create(Tenant)
            });
        Assert.Equal(new RealtimeChange("workspace-inventory", Tenant, Resource, Revision: 6, Generation: 4), hints[2]);
    }

    [Theory]
    [InlineData("created")]
    [InlineData("updated")]
    [InlineData("deleted")]
    public async Task ResourceGroupCrudHintsKeepGroupIdentityAndDoNotCopyProviderPayloads(string operation)
    {
        var hints = new List<RealtimeChange>();
        var consumer = new NotifyResourceGroupChangedConsumer((change, _) =>
        {
            hints.Add(change);
            return Task.CompletedTask;
        });
        var core = EventCore.Create(Tenant);
        var tags = ImmutableHashSet.Create("private-tag");
        await (operation switch
        {
            "created" => consumer.HandleAsync(
                new ResourceGroupCreatedEvent(Group, "private-name", "private-description", tags, 7)
                {
                    EventCore = core
                }),
            "updated" => consumer.HandleAsync(new ResourceGroupUpdatedEvent(Group,
                    "private-name",
                    "private-description",
                    new(UpdateLog<string>.Of("before", "after"),
                        UpdateLog<ImmutableHashSet<string>>.Of(ImmutableHashSet<string>.Empty, tags)),
                    7)
            {
                EventCore = core
            }),
            "deleted" => consumer.HandleAsync(
                new ResourceGroupDeletedEvent(Group, "private-name", "private-description", tags, 7)
                {
                    EventCore = core
                }),
            _ => throw new InvalidOperationException()
        });
        Assert.Equal(new("resource-groups", Tenant, Group, Group, 7), hints[0]);
        Assert.Equal(new RealtimeChange("workspace-inventory", Tenant, ResourceGroupId: Group), hints[1]);
        Assert.Equal(2, hints.Count);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(hints));
    }

    [Theory]
    [InlineData("registered")]
    [InlineData("updated")]
    [InlineData("unregistered")]
    public async Task RegistrationHintsUseLogicalResourceIdentityForInventoryAndGroupIdentityForGroups(string operation)
    {
        var hints = new List<RealtimeChange>();
        var consumer = new NotifyResourceGroupChangedConsumer((change, _) =>
        {
            hints.Add(change);
            return Task.CompletedTask;
        });
        var core = EventCore.Create(Tenant);
        await (operation switch
        {
            "registered" => consumer.HandleAsync(new ResourceRegisteredEvent(Group, "acme/plugin-resource", Resource)
            {
                EventCore = core
            }),
            "updated" => consumer.HandleAsync(
                new ResourceRegistrationUpdatedEvent(Group, "acme/plugin-resource", Resource) { EventCore = core }),
            "unregistered" => consumer.HandleAsync(
                new ResourceUnregisteredEvent(Group, "acme/plugin-resource", Resource) { EventCore = core }),
            _ => throw new InvalidOperationException()
        });
        Assert.Equal(new("resource-groups", Tenant, Group, Group), hints[0]);
        Assert.Equal(new RealtimeChange("workspace-inventory", Tenant, Resource, Group), hints[1]);
        Assert.Equal(2, hints.Count);
    }

    [Fact]
    public void ResourceGroupsRegistersItsTwoTenantTopicsOnce()
    {
        var services = new ServiceCollection();
        services.AddResourceGroupsFeature(new ConfigurationBuilder().Build());
        var topics = services.Where(descriptor => descriptor.ServiceType == typeof(RealtimeTopic))
            .Select(descriptor => Assert.IsType<RealtimeTopic>(descriptor.ImplementationInstance)).ToArray();
        Assert.Equal(["resource-groups", "workspace-inventory"], topics.Select(topic => topic.Name));
        Assert.All(topics, topic => Assert.Equal(RealtimeScope.Tenant, topic.Scope));
    }
}
