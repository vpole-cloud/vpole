using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Events.Abstractions;
using Pole.Features.Realtime.Contracts;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.ResourceConnections.Application.Realtime;
using Pole.Features.ResourceConnections.Events;
using Xunit;

namespace Pole.Features.ResourceConnections.Tests;

public sealed class ResourceConnectionRealtimeTests
{
    [Fact]
    public async Task ExistingAndWorkerEventsPreserveTenantConnectionAndVersions()
    {
        var notices = new List<RealtimeChange>();
        var consumer = new NotifyResourceConnectionChangedConsumer((change, _) =>
        {
            notices.Add(change);
            return Task.CompletedTask;
        });
        await consumer.HandleAsync(new ResourceConnectionCreatedEvent("connection", 1)
        {
            EventCore = EventCore.Create("tenant")
        });
        await consumer.HandleAsync(new ResourceConnectionReconciliationRequestedEvent("connection", 2)
        {
            EventCore = EventCore.Create("tenant")
        });
        await consumer.HandleAsync(new ResourceConnectionDeletionRequestedEvent("connection", 3)
        {
            EventCore = EventCore.Create("tenant")
        });
        await consumer.HandleAsync(new ResourceConnectionChangedEvent("connection", 8, 3)
        {
            EventCore = EventCore.Create("tenant")
        });
        Assert.Equal(4, notices.Count);
        Assert.All(notices,
            notice =>
            {
                Assert.Equal("tenant", notice.TenantId);
                Assert.Equal("connection", notice.ResourceId);
            });
        Assert.Equal(new RealtimeChange("resource-connections", "tenant", "connection", Revision: 8, Generation: 3),
            notices[^1]);
    }

    [Fact]
    public void ConnectionsRegistersOneTenantTopicWithoutRuntimeResolution()
    {
        var services = new ServiceCollection().AddResourceConnectionsFeature();
        Assert.Equal(new RealtimeTopic("resource-connections", RealtimeScope.Tenant),
            Assert.Single(services, item => item.ServiceType == typeof(RealtimeTopic)).ImplementationInstance);
    }
}
