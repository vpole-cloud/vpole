// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Realtime.Application;
using Pole.Features.Realtime.Application.Redis;
using Pole.Features.Realtime.Contracts;
using Xunit;

namespace Pole.Features.Realtime.Tests;

public sealed class RedisInfrastructureRegistrationTests
{
    [Fact]
    public async Task DisabledFeatureResolvesItsRegistryWithoutRemovedProvidersOrDistributedStores()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Realtime:Enabled"] = "false",
                    ["Realtime:EnabledTopics:0"] = "removed-provider",
                })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRealtimeFeature(configuration, development: false);
        services.AddRealtimeRedis(configuration, development: false);
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetRequiredService<RealtimeTopicRegistry>().Find("removed-provider"));
        Assert.Null(provider.GetService<IRealtimeTicketStore>());
        Assert.Null(provider.GetService<IRealtimeSubscriptions>());
        await provider.GetRequiredService<InvalidateRealtimeTenants>()("unregistered");
    }

    [Fact]
    public void DisabledFeatureDoesNotRegisterRedisOrTicketStores()
    {
        var services = new ServiceCollection();
        services.AddRealtimeRedis(new ConfigurationBuilder().Build(), development: false);
        Assert.Empty(services);
    }

    [Fact]
    public void EnabledFeatureRequiresDedicatedRedisConnection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Realtime:Enabled"] = "true",
                })
            .Build();
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddRealtimeRedis(configuration, development: false));
        Assert.Contains("ConnectionStrings:realtime", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PersistentKeyringAlwaysRequiresEncryptionCertificate(bool development)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Realtime:Enabled"] = "true",
                    ["ConnectionStrings:realtime"] = "localhost:6379",
                })
            .Build();
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddRealtimeRedis(configuration, development));
        Assert.Contains("certificate", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
