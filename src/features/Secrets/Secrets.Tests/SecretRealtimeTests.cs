// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

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
        var consumer = new NotifySecretChangedConsumer((change, _) =>
        {
            changes.Add(change);
            return Task.CompletedTask;
        });
        var deleted = new SecretChangedEvent(
            "secret",
            4,
            7,
            new SecretDeletedChange(),
            "database",
            EventCore.Create("tenant"));
        var restored = JsonSerializer.Deserialize<SecretChangedEvent>(JsonSerializer.Serialize(deleted))!;
        await consumer.HandleAsync(restored);
        await consumer.HandleAsync(
            new SecretProjectionChangedEvent("secret", "projection", "group", "binding", "deployment", 4, 7, 12)
            {
                EventCore = EventCore.Create("tenant"),
            });
        Assert.Equal(
            [
                new RealtimeChange("secrets", "tenant", "secret", Revision: 7),
                new RealtimeChange("secrets", "tenant", "secret", "group", 7, ObservationVersion: 12),
            ],
            changes);
    }

    [Fact]
    public void SecretsRegistersOneTenantTopicWithoutResolvingRuntimeProviders()
    {
        var services = new ServiceCollection().AddSecretsFeature();
        var topic = Assert.Single(services, item => item.ServiceType == typeof(RealtimeTopic));
        Assert.Equal(new RealtimeTopic("secrets", RealtimeScope.Tenant), topic.ImplementationInstance);
    }
}
