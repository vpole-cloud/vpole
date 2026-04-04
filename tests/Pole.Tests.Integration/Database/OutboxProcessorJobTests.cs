using System.Reflection;
using Pole.Features.Outbox;
using Xunit;

namespace Pole.Tests.Integration.Database;

public sealed class OutboxProcessorJobTests
{
    [Fact]
    public void EventTypeCache_Includes_FSharpPostgresEvents()
    {
        var cacheField = typeof(OutboxProcessorJob).GetField("EventTypeCache", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(cacheField);

        var cache = cacheField!.GetValue(null);
        Assert.NotNull(cache);

        var containsKeyMethod = cache!.GetType().GetMethod("ContainsKey");
        Assert.NotNull(containsKeyMethod);

        var containsCreateEvent = (bool)containsKeyMethod!.Invoke(cache, ["create-postgres-cluster-requested"])!;
        var containsUpdateEvent = (bool)containsKeyMethod.Invoke(cache, ["update-postgres-cluster-requested"])!;
        var containsDeleteEvent = (bool)containsKeyMethod.Invoke(cache, ["delete-postgres-cluster-requested"])!;

        Assert.True(containsCreateEvent);
        Assert.True(containsUpdateEvent);
        Assert.True(containsDeleteEvent);
    }
}
