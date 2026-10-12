using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Events.Abstractions;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Realtime;
using Pole.Features.Secrets.Events;
using Xunit;

namespace Pole.Features.Secrets.Tests;

public sealed class SecretRealtimeTests
{
    [Fact]
    public async Task MetadataAndProjectionConsumersRetainTenantAndSecretIdentityIncludingHistoricalDeletion()
    {
        var changes = new List<RealtimeChange>();
        var consumer = new NotifySecretChangedConsumer((change, _) => { changes.Add(change); return Task.CompletedTask; });
        var deleted = new SecretChangedEvent("secret", 4, 7, new SecretDeletedChange()) { EventCore = EventCore.Create("tenant") };
        var historical = JsonSerializer.Deserialize<SecretChangedEvent>(JsonSerializer.Serialize(deleted))!;
        await consumer.HandleAsync(historical);
        await consumer.HandleAsync(new SecretProjectionChangedEvent("secret", "projection", "group", "binding", "deployment", 4, 7, 12)
        { EventCore = EventCore.Create("tenant") });
        Assert.Equal([new RealtimeChange("secrets", "tenant", "secret", Revision: 7),
            new RealtimeChange("secrets", "tenant", "secret", "group", 7, ObservationVersion: 12)], changes);
    }

    [Fact]
    public void SecretsRegistersOneTenantTopicWithoutResolvingRuntimeProviders()
    {
        var services = new ServiceCollection().AddSecretsFeature();
        var topic = Assert.Single(services, item => item.ServiceType == typeof(RealtimeTopic));
        Assert.Equal(new RealtimeTopic("secrets", RealtimeScope.Tenant), topic.ImplementationInstance);
    }
}
