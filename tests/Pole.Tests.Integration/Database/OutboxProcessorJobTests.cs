using System.Reflection;
using Pole.Features.Outbox;
using Pole.Features.WebServices.Events;
using Xunit;

namespace Pole.Tests.Integration.Database;

public sealed class OutboxProcessorJobTests
{
    [Fact]
    public void EventTypeCache_Includes_FSharpPostgresEvents()
    {
        var buildMethod = typeof(OutboxProcessorJob).GetMethod("BuildEventTypeCache", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(buildMethod);
        var eventAssemblies = new[]
        {
            typeof(CreateEvent).Assembly,
            typeof(Pole.Features.Postgres.Events.CreateEvent).Assembly,
            typeof(Pole.Features.RedisServices.Events.CreateEvent).Assembly
        };
        var cache = buildMethod.Invoke(null, [eventAssemblies]);
        Assert.NotNull(cache);

        var containsKeyMethod = cache.GetType().GetMethod("ContainsKey");
        Assert.NotNull(containsKeyMethod);

        var containsCreateEvent = (bool)containsKeyMethod.Invoke(cache, ["create-postgres-cluster-requested"])!;
        var containsUpdateEvent = (bool)containsKeyMethod.Invoke(cache, ["update-postgres-cluster-requested"])!;
        var containsDeleteEvent = (bool)containsKeyMethod.Invoke(cache, ["delete-postgres-cluster-requested"])!;
        var containsRedisCreateEvent = (bool)containsKeyMethod.Invoke(cache, ["create-redis-service-requested"])!;
        var containsRedisUpdateEvent = (bool)containsKeyMethod.Invoke(cache, ["update-redis-service"])!;
        var containsRedisDeleteEvent = (bool)containsKeyMethod.Invoke(cache, ["delete-redis-service"])!;

        Assert.True(containsCreateEvent);
        Assert.True(containsUpdateEvent);
        Assert.True(containsDeleteEvent);
        Assert.True(containsRedisCreateEvent);
        Assert.True(containsRedisUpdateEvent);
        Assert.True(containsRedisDeleteEvent);
    }
}
