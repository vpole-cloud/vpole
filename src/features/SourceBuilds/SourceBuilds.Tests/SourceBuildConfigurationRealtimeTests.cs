using Pole.Features.Events.Abstractions;
using Pole.Features.Realtime.Contracts;
using Pole.Features.SourceBuilds.Application.Realtime;
using Pole.Features.SourceBuilds.Events;
using Xunit;

namespace Pole.Features.SourceBuilds.Tests;

public sealed class SourceBuildConfigurationRealtimeTests
{
    [Fact]
    public async Task ConfigurationHintUsesTenantAndLogicalParentWebService()
    {
        RealtimeChange? notice = null;
        var consumer = new NotifySourceBuildConfigurationChangedConsumer((change, _) =>
        {
            notice = change;
            return Task.CompletedTask;
        });
        await consumer.HandleAsync(new("configuration", "web-service", "api-build", 1)
        {
            EventCore = EventCore.Create("tenant")
        });
        Assert.Equal(new RealtimeChange("source-builds", "tenant", "web-service"), notice);
    }
}
