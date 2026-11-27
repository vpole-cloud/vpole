// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitProviders.Application;
using Pole.Features.GitProviders.Application.Realtime;
using Pole.Features.GitProviders.Events;
using Pole.Features.Realtime.Contracts;
using Xunit;

namespace Pole.Features.GitProviders.Tests;

public sealed class GitRealtimeTests
{
    [Fact]
    public async Task ConnectionAndRepositoryHintsRetainOnlyTenantScope()
    {
        var notices = new List<RealtimeChange>();
        var consumer = new NotifyGitMetadataChangedConsumer((change, _) =>
        {
            notices.Add(change);
            return Task.CompletedTask;
        });
        await consumer.HandleAsync(new GitProviderConnectionChangedEvent("connection", EventCore.Create("tenant")));
        await consumer.HandleAsync(
            new SourceRepositoryChangedEvent(
                "repository",
                "connection",
                "application-source",
                1,
                EventCore.Create("tenant")));
        Assert.Equal(2, notices.Count);
        Assert.All(notices, notice => Assert.Equal(new RealtimeChange("git", "tenant"), notice));
    }

    [Fact]
    public void GitRegistersOneTenantTopicWithoutResolvingProviderClients()
    {
        var services = new ServiceCollection().AddGitProvidersFeature(new ConfigurationBuilder().Build());
        Assert.Equal(
            new RealtimeTopic("git", RealtimeScope.Tenant),
            Assert.Single(services, item => item.ServiceType == typeof(RealtimeTopic)).ImplementationInstance);
    }
}
