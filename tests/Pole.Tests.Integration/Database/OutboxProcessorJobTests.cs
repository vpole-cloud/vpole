using System.Reflection;
using Pole.Features.Outbox.Application;
using Pole.Features.WebServices.Events;
using Xunit;

namespace Pole.Tests.Integration.Database;

public sealed class OutboxProcessorJobTests
{
    [Fact]
    public void ProcessOutboxMessages_PreventsConcurrentHangfireRuns()
    {
        var processMethod = typeof(OutboxProcessorJob).GetMethod(nameof(OutboxProcessorJob.ProcessOutboxMessagesAsync));

        Assert.NotNull(processMethod);
        Assert.Contains(
            processMethod.GetCustomAttributes(),
            attribute => attribute.GetType().Name == "DisableConcurrentExecutionAttribute");
    }

    [Fact]
    public void EventTypeCache_Includes_FSharpPostgresEvents()
    {
        var buildMethod = typeof(OutboxProcessorJob).GetMethod("BuildEventTypeCache", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(buildMethod);
        var eventAssemblies = new[]
        {
            typeof(WebServiceCreatedEvent).Assembly,
            typeof(Pole.Features.DnsRecords.Events.CommitDomainNameEvent).Assembly,
            typeof(Pole.Features.Postgres.Events.PostgresClusterCreatedEvent).Assembly,
            typeof(Pole.Features.RedisServices.Events.RedisServiceCreatedEvent).Assembly
        };
        var cache = buildMethod.Invoke(null, [eventAssemblies]);
        Assert.NotNull(cache);

        var containsKeyMethod = cache.GetType().GetMethod("ContainsKey");
        Assert.NotNull(containsKeyMethod);

        var containsCreateEvent = (bool)containsKeyMethod.Invoke(cache, ["postgres-cluster-created"])!;
        var containsUpdateEvent = (bool)containsKeyMethod.Invoke(cache, ["postgres-cluster-updated"])!;
        var containsDeleteEvent = (bool)containsKeyMethod.Invoke(cache, ["postgres-cluster-deleted"])!;
        var containsRedisCreateEvent = (bool)containsKeyMethod.Invoke(cache, ["redis-service-created"])!;
        var containsRedisUpdateEvent = (bool)containsKeyMethod.Invoke(cache, ["redis-service-updated"])!;
        var containsRedisDeleteEvent = (bool)containsKeyMethod.Invoke(cache, ["redis-service-deleted"])!;
        var containsDnsCommitEvent = (bool)containsKeyMethod.Invoke(cache, ["commit-domain-name"])!;

        Assert.True(containsCreateEvent);
        Assert.True(containsUpdateEvent);
        Assert.True(containsDeleteEvent);
        Assert.True(containsRedisCreateEvent);
        Assert.True(containsRedisUpdateEvent);
        Assert.True(containsRedisDeleteEvent);
        Assert.True(containsDnsCommitEvent);
    }
}

