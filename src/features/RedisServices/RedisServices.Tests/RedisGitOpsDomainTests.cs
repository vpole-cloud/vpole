using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.RedisServices.Domain;
using Xunit;

namespace Pole.Features.RedisServices.Tests;

public sealed class RedisGitOpsDomainTests
{
    [Fact]
    public void FactoryExplicitlyInitializesPlanningFenceAndPreparedActivationPreservesIt()
    {
        var prepared = RedisFactory.Create(
            "507f1f77bcf86cd799439001",
            "cache",
            "507f1f77bcf86cd799439002",
            new RedisSpec(new Storage(4, "507f1f77bcf86cd799439003")),
            RedisFactory.DefaultCreateOptions with { Lifecycle = new PreparedRedisLifecycle() });
        Assert.Equal(0, prepared.PlanningFence);
        Assert.Equal(1, prepared.Revision);
        var fenced = prepared with { PlanningFence = 7 };
        var configured = fenced.ActivatePrepared(ImmutableHashSet.Create("production"));
        Assert.IsType<ActiveRedisLifecycle>(configured.Lifecycle);
        Assert.Equal(7, configured.PlanningFence);
        Assert.Equal(2, configured.Revision);
        Assert.Equal(prepared.Spec, configured.Spec);
        Assert.Equal(prepared.Id, configured.Id);
        Assert.Equal(prepared.ResourceGroupId, configured.ResourceGroupId);
    }

    [Fact]
    public void CurrentPreparedAndConfiguredDocumentsRoundTripWithoutDefaults()
    {
        var prepared = RedisFactory.Create(
            "507f1f77bcf86cd799439001",
            "cache",
            "507f1f77bcf86cd799439002",
            new RedisSpec(new Storage(4, "507f1f77bcf86cd799439003")),
            RedisFactory.DefaultCreateOptions with { Lifecycle = new PreparedRedisLifecycle() });
        var configured = prepared.ActivatePrepared(ImmutableHashSet.Create("production"));
        foreach (var resource in new[] { prepared, configured })
        {
            var document = resource.ToBsonDocument();
            var restored = BsonSerializer.Deserialize<Redis>(document);
            Assert.Equal(document, restored.ToBsonDocument());
            Assert.Equal(resource.Lifecycle.GetType(), restored.Lifecycle.GetType());
            Assert.Equal(0, restored.PlanningFence);
        }

        Assert.Throws<InvalidOperationException>(() => configured.BeginDeletion(DateTime.UtcNow)
            .ActivatePrepared(ImmutableHashSet<string>.Empty));
    }
}
