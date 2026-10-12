using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Events.Abstractions;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Postgres.Application;
using Pole.Features.Postgres.Application.Realtime;
using Pole.Features.Postgres.Events;
using Pole.Features.ResourceDeployments.Events;
using Xunit;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresRealtimeTests
{
    private const string Tenant = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Group = "bbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Resource = "postgres-resource";

    [Theory]
    [InlineData("created")]
    [InlineData("updated")]
    [InlineData("deleted")]
    public async Task CrudHintsContainOnlyTenantResourceGroupAndRevision(string operation)
    {
        var hints = new List<RealtimeChange>();
        var consumer = Consumer(hints);
        var spec = new PostgresClusterSpecSnapshot(1, 7, "private-storage-profile", 17, PostgresDeploymentModeSnapshot.StatefulSet);
        var core = EventCore.Create(Tenant);
        await (operation switch
        {
            "created" => consumer.HandleAsync(new PostgresClusterCreatedEvent(core, Resource, "private-name", Group, 6, spec), default),
            "updated" => consumer.HandleAsync(new PostgresClusterUpdatedEvent(core, Resource, "private-name", Group, 6,
                new PostgresClusterUpdatedEventChanges(UpdateLog<PostgresClusterSpecSnapshot>.Of(spec, spec))), default),
            "deleted" => consumer.HandleAsync(new PostgresClusterDeletedEvent(core, Resource, "private-name", Group, 6, spec), default),
            _ => throw new InvalidOperationException()
        });
        Assert.Equal(new RealtimeChange("postgres", Tenant, Resource, Group, 6), Assert.Single(hints));
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
                "requested" => consumer.HandleAsync(new ResourceDeploymentReconciliationRequestedEvent("deployment", "cell", 4, 6) { EventCore = core, ResourceType = type, ResourceId = Resource, ResourceGroupId = Group }, default),
                "deletion-requested" => consumer.HandleAsync(new ResourceDeploymentDeletionRequestedEvent("deployment", "cell", 4, 6) { EventCore = core, ResourceType = type, ResourceId = Resource, ResourceGroupId = Group }, default),
                "started" => consumer.HandleAsync(new ResourceDeploymentReconciliationStartedEvent("deployment", type, Resource, Group, "cell", 4, "Apply", 6) { EventCore = core }, default),
                "applied" => consumer.HandleAsync(new ResourceDeploymentAppliedEvent("deployment", type, Resource, "cell", 4, 6) { EventCore = core, ResourceGroupId = Group }, default),
                "failed" => consumer.HandleAsync(new ResourceDeploymentFailedEvent("deployment", type, Resource, "cell", 4, "Apply", "private-failure", true, 6) { EventCore = core, ResourceGroupId = Group }, default),
                "deleted" => consumer.HandleAsync(new ResourceDeploymentDeletedEvent("deployment", type, Resource, "cell", 4, 6) { EventCore = core, ResourceGroupId = Group }, default),
                _ => throw new InvalidOperationException()
            });
        }
        await Send("pole/redis");
        Assert.Empty(hints);
        await Send("pole/postgresql");
        Assert.Equal(new RealtimeChange("postgres", Tenant, Resource, Group, 6, 4), Assert.Single(hints));
        Assert.DoesNotContain("private-failure", JsonSerializer.Serialize(hints));
    }

    [Fact]
    public async Task HistoricalRequestsWithoutAdditiveIdentityInvalidateTenantReadsBroadly()
    {
        var hints = new List<RealtimeChange>();
        var payload = "{\"resourceDeploymentId\":\"deployment\",\"runtimeCellId\":\"cell\",\"generation\":4,\"resourceRevision\":6,\"eventCore\":{\"tenantId\":\"" + Tenant + "\"}}";
        await Consumer(hints).HandleAsync(JsonSerializer.Deserialize<ResourceDeploymentReconciliationRequestedEvent>(payload, EventExtensions.SerializerOptions)!, default);
        await Consumer(hints).HandleAsync(JsonSerializer.Deserialize<ResourceDeploymentDeletionRequestedEvent>(payload, EventExtensions.SerializerOptions)!, default);
        Assert.Equal(2, hints.Count);
        Assert.All(hints, hint => Assert.Equal(new RealtimeChange("postgres", Tenant, Revision: 6, Generation: 4), hint));
        await Consumer(hints).HandleAsync(new ResourceDeploymentDeletedEvent("deployment", "pole/postgresql", Resource, "cell", 4, 6) { EventCore = EventCore.Create(Tenant) }, default);
        Assert.Equal(new RealtimeChange("postgres", Tenant, Resource, Revision: 6, Generation: 4), hints[2]);
    }

    [Fact]
    public void RegistersItsTenantTopicOnce()
    {
        var services = new ServiceCollection();
        services.AddPostgresFeature(new ConfigurationBuilder().Build());
        Assert.Equal(new RealtimeTopic("postgres", RealtimeScope.Tenant), Assert.Single(services
            .Where(descriptor => descriptor.ServiceType == typeof(RealtimeTopic)).Select(descriptor => descriptor.ImplementationInstance)));
    }

    private static NotifyPostgresChangedConsumer Consumer(List<RealtimeChange> hints) =>
        new((change, _) => { hints.Add(change); return Task.CompletedTask; });
}
