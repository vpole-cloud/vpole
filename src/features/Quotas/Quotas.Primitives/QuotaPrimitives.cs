using System.Collections.Immutable;

namespace Pole.Features.Quotas.Primitives;

public readonly record struct QuotaKey
{
    public QuotaKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Quota key is required.", nameof(value));

        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record QuotaValue(
    QuotaKey Key,
    long Value);

public sealed record QuotaDelta
{
    private QuotaDelta(ImmutableList<QuotaValue> values)
    {
        Values = values;
    }

    public static QuotaDelta Empty { get; } = new(ImmutableList<QuotaValue>.Empty);

    public ImmutableList<QuotaValue> Values { get; }
    public bool IsEmpty => Values.Count == 0;

    public long this[QuotaKey key] =>
        Values.FirstOrDefault(value => value.Key == key)?.Value ?? 0;

    public static QuotaDelta Create(IEnumerable<QuotaValue> values) =>
        new(Normalize(values));

    public static QuotaDelta From(QuotaKey key, long value) =>
        Create([new QuotaValue(key, value)]);

    public static QuotaDelta Difference(QuotaDelta newer, QuotaDelta older) =>
        Create(
            newer.Values.Select(value => value.Key)
                .Concat(older.Values.Select(value => value.Key))
                .Distinct()
                .Select(key => new QuotaValue(key, checked(newer[key] - older[key]))));

    public static QuotaDelta PositivePart(QuotaDelta delta) =>
        Create(delta.Values.Where(value => value.Value > 0));

    public static QuotaDelta NegativePart(QuotaDelta delta) =>
        Create(delta.Values.Where(value => value.Value < 0));

    private static ImmutableList<QuotaValue> Normalize(IEnumerable<QuotaValue> values) =>
        values
            .GroupBy(value => value.Key)
            .Select(group => new QuotaValue(
                group.Key,
                group.Aggregate(0L, (sum, value) => checked(sum + value.Value))))
            .Where(value => value.Value != 0)
            .OrderBy(value => value.Key.Value, StringComparer.Ordinal)
            .ToImmutableList();
}

public readonly record struct QuotaResourceKey
{
    private QuotaResourceKey(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static QuotaResourceKey Create(string resourceKind, string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceKind))
            throw new ArgumentException("Resource kind is required.", nameof(resourceKind));
        if (string.IsNullOrWhiteSpace(resourceId))
            throw new ArgumentException("Resource id is required.", nameof(resourceId));

        return new QuotaResourceKey($"{resourceKind.Trim()}:{resourceId.Trim()}");
    }

    public static QuotaResourceKey Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Quota resource key is required.", nameof(value));

        return new QuotaResourceKey(value.Trim());
    }

    public override string ToString() => Value;
}
