using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.Shared.Models;

namespace Pole.Features.RedisServices.Domain;

public sealed record Redis(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string Name,
    string ResourceGroupId,
    RedisSpec Spec,
    ImmutableHashSet<string> Tags,
    ImmutableList<ServiceEvent> Events,
    long Revision,
    long PlanningFence,
    RedisLifecycle Lifecycle)
{
    public Redis UpdateDesiredState(
        RedisSpec spec,
        ImmutableHashSet<string> tags)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(tags);
        return this with
        {
            Spec = spec,
            Tags = tags,
            Revision = checked(Revision + 1)
        };
    }

    public Redis ActivatePrepared(ImmutableHashSet<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (Lifecycle is not (PreparedRedisLifecycle or ActiveRedisLifecycle))
            throw new InvalidOperationException("A deleting Redis service cannot be configured.");

        return this with
        {
            Tags = tags,
            Lifecycle = new ActiveRedisLifecycle(),
            Revision = checked(Revision + 1)
        };
    }

    public Redis BeginDeletion(DateTime requestedAtUtc)
    {
        if (Lifecycle is DeletingRedisLifecycle)
            return this;

        var normalizedRequestedAtUtc = requestedAtUtc.Kind switch
        {
            DateTimeKind.Utc => requestedAtUtc,
            DateTimeKind.Local => requestedAtUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(requestedAtUtc, DateTimeKind.Utc)
        };
        return this with
        {
            Lifecycle = new DeletingRedisLifecycle(normalizedRequestedAtUtc),
            Revision = checked(Revision + 1)
        };
    }

    private void EnsureActive()
    {
        if (Lifecycle is not ActiveRedisLifecycle)
            throw new InvalidOperationException("A deleting Redis service cannot be updated.");
    }
}

public sealed record RedisSpec(
    Storage Storage);

public sealed record Storage(
    int SizeGb,
    string StorageProfileId);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(PreparedRedisLifecycle), typeof(ActiveRedisLifecycle), typeof(DeletingRedisLifecycle))]
public abstract record RedisLifecycle;

public sealed record PreparedRedisLifecycle : RedisLifecycle;

public sealed record ActiveRedisLifecycle : RedisLifecycle;

public sealed record DeletingRedisLifecycle(DateTime RequestedAtUtc) : RedisLifecycle;

public static class RedisFactory
{
    public sealed record RedisCreateOptions(
        string Id,
        ImmutableHashSet<string> Tags,
        ImmutableList<ServiceEvent> Events,
        long Revision,
        RedisLifecycle Lifecycle);

    public static RedisCreateOptions DefaultCreateOptions =>
        new(
            Id: ObjectId.GenerateNewId().ToString(),
            Tags: ImmutableHashSet<string>.Empty,
            Events: ImmutableList<ServiceEvent>.Empty,
            Revision: 1,
            Lifecycle: new ActiveRedisLifecycle());

    public static Redis Create(
        string tenantId,
        string name,
        string resourceGroupId,
        RedisSpec spec,
        RedisCreateOptions options) =>
        new(
            Id: options.Id,
            TenantId: tenantId,
            Name: name,
            ResourceGroupId: resourceGroupId,
            Spec: spec,
            Tags: options.Tags,
            Events: options.Events,
            Revision: options.Revision,
            PlanningFence: 0,
            Lifecycle: options.Lifecycle);
}
