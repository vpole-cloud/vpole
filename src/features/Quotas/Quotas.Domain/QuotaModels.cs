// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Serializers;
using Pole.Features.Quotas.Primitives;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.Quotas.Domain;

public sealed record TenantQuotaPolicy(
    [property: BsonId] string TenantId,
    ImmutableList<QuotaValue> Limits,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc
) : ITenantOwned;

public sealed record TenantQuotaUsage(
    [property: BsonId] string TenantId,
    ImmutableList<QuotaValue> Values,
    long AdmissionRevision,
    DateTime UpdatedAtUtc
) : ITenantOwned;

public static class QuotaReservationStateKinds
{
    public const string Active = "active";
    public const string Committed = "committed";
    public const string Cancelled = "cancelled";
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(ActiveQuotaReservationState),
    typeof(CommittedQuotaReservationState),
    typeof(CancelledQuotaReservationState))]
public abstract record QuotaReservationState(
    string Kind,
    DateTime CleanupAfterUtc
);

[BsonDiscriminator(QuotaReservationStateKinds.Active)]
public sealed record ActiveQuotaReservationState(
    DateTime ExpiresAtUtc,
    DateTime CleanupAfterUtc
) : QuotaReservationState(QuotaReservationStateKinds.Active, CleanupAfterUtc);

[BsonDiscriminator(QuotaReservationStateKinds.Committed)]
public sealed record CommittedQuotaReservationState(
    DateTime CommittedAtUtc,
    DateTime CleanupAfterUtc
) : QuotaReservationState(QuotaReservationStateKinds.Committed, CleanupAfterUtc);

[BsonDiscriminator(QuotaReservationStateKinds.Cancelled)]
public sealed record CancelledQuotaReservationState(
    DateTime CancelledAtUtc,
    DateTime CleanupAfterUtc
) : QuotaReservationState(QuotaReservationStateKinds.Cancelled, CleanupAfterUtc);

public sealed record QuotaReservation(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    ImmutableList<QuotaValue> Delta,
    [property: BsonSerializer(typeof(QuotaResourceKeyBsonSerializer))]
    QuotaResourceKey ResourceKey,
    DateTime CreatedAtUtc,
    QuotaReservationState State
) : ITenantOwned;

public sealed class QuotaResourceKeyBsonSerializer : StructSerializerBase<QuotaResourceKey>
{
    public override QuotaResourceKey Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args) =>
        QuotaResourceKey.Parse(context.Reader.ReadString());

    public override void Serialize(
        BsonSerializationContext context,
        BsonSerializationArgs args,
        QuotaResourceKey value)
    {
        if (string.IsNullOrWhiteSpace(value.Value))
            throw new BsonSerializationException("Quota resource key must have a value before it can be persisted.");

        context.Writer.WriteString(value.Value);
    }
}

public static class QuotaDomainFactory
{
    public static TenantQuotaUsage CreateUsage(string tenantId, DateTime nowUtc) => new(
        tenantId,
        ImmutableList<QuotaValue>.Empty,
        0,
        nowUtc);

    public static TenantQuotaPolicy CreatePolicy(
        string tenantId,
        QuotaDelta limits,
        DateTime nowUtc,
        DateTime? createdAtUtc = null) => new(
        tenantId,
        QuotaValues.Normalize(limits.Values),
        createdAtUtc ?? nowUtc,
        nowUtc);

    public static QuotaReservation CreateReservation(
        string tenantId,
        QuotaDelta delta,
        QuotaResourceKey resourceKey,
        DateTime nowUtc,
        TimeSpan activeLifetime,
        TimeSpan finalizedHistory)
    {
        var expiresAtUtc = nowUtc.Add(activeLifetime);
        return new QuotaReservation(
            ObjectId.GenerateNewId().ToString(),
            tenantId,
            QuotaValues.Normalize(delta.Values),
            resourceKey,
            nowUtc,
            new ActiveQuotaReservationState(expiresAtUtc, expiresAtUtc.Add(finalizedHistory)));
    }

    public static CommittedQuotaReservationState CommitReservation(DateTime nowUtc, TimeSpan finalizedHistory) =>
        new(nowUtc, nowUtc.Add(finalizedHistory));

    public static CancelledQuotaReservationState CancelReservation(DateTime nowUtc, TimeSpan finalizedHistory) =>
        new(nowUtc, nowUtc.Add(finalizedHistory));
}

public static class QuotaValues
{
    public static ImmutableList<QuotaValue> Normalize(IEnumerable<QuotaValue> values) => values
        .GroupBy(value => value.Key)
        .Select(group => new QuotaValue(group.Key, group.Aggregate(0L, (sum, value) => checked(sum + value.Value))))
        .Where(value => value.Value != 0)
        .OrderBy(value => value.Key.Value, StringComparer.Ordinal)
        .ToImmutableList();

    public static long GetValue(IEnumerable<QuotaValue> values, QuotaKey key) =>
        values.FirstOrDefault(value => value.Key == key)?.Value ?? 0;

    public static ImmutableList<QuotaValue> Add(IEnumerable<QuotaValue> first, IEnumerable<QuotaValue> second) =>
        Normalize(first.Concat(second));

    public static ImmutableList<QuotaValue> AddAndClampToZero(
        IEnumerable<QuotaValue> first,
        IEnumerable<QuotaValue> second) => Normalize(first.Concat(second))
        .Select(value => new QuotaValue(value.Key, Math.Max(0, value.Value)))
        .Where(value => value.Value != 0)
        .ToImmutableList();

    public static ImmutableList<QuotaValue> Sum(IEnumerable<IEnumerable<QuotaValue>> sets) =>
        Normalize(sets.SelectMany(set => set));
}
