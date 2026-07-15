using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.ResultType;

namespace Pole.Features.Quotas.Contracts;

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

public abstract class Quota
{
    public abstract QuotaKey Key { get; }
    public abstract string Unit { get; }
}

public abstract class Quota<TInput> : Quota
{
    public abstract long Calculate(TInput input);

    public QuotaValue Measure(TInput input) => new(Key, Calculate(input));
}

public sealed record WorkloadQuotaInput(
    long CpuMillisPerInstance,
    long MemoryMiPerInstance,
    long Instances);

public sealed class CpuQuota : Quota<WorkloadQuotaInput>
{
    public static QuotaKey QuotaKey { get; } = new("CpuMillis");

    public override QuotaKey Key => QuotaKey;
    public override string Unit => "millicores";

    public override long Calculate(WorkloadQuotaInput input) =>
        checked(input.CpuMillisPerInstance * input.Instances);
}

public sealed class RamQuota : Quota<WorkloadQuotaInput>
{
    public static QuotaKey QuotaKey { get; } = new("MemoryMi");

    public override QuotaKey Key => QuotaKey;
    public override string Unit => "MiB";

    public override long Calculate(WorkloadQuotaInput input) =>
        checked(input.MemoryMiPerInstance * input.Instances);
}

public sealed class WorkloadInstancesQuota : Quota<WorkloadQuotaInput>
{
    public static QuotaKey QuotaKey { get; } = new("WorkloadInstances");

    public override QuotaKey Key => QuotaKey;
    public override string Unit => "instances";

    public override long Calculate(WorkloadQuotaInput input) => input.Instances;
}

public interface IQuotaProfile
{
    Type SpecType { get; }
    IReadOnlyCollection<QuotaKey> Keys { get; }
    QuotaDelta Calculate(object spec);
}

public interface IQuotaProfile<in TSpec> : IQuotaProfile
{
    QuotaDelta Calculate(TSpec spec);
}

public abstract class QuotaProfile<TSpec> : IQuotaProfile<TSpec>
{
    public Type SpecType => typeof(TSpec);
    public abstract IReadOnlyCollection<QuotaKey> Keys { get; }
    public abstract QuotaDelta Calculate(TSpec spec);

    QuotaDelta IQuotaProfile.Calculate(object spec) =>
        spec is TSpec typedSpec
            ? Calculate(typedSpec)
            : throw new InvalidOperationException(
                $"Quota profile for {typeof(TSpec).FullName} cannot calculate {spec.GetType().FullName}.");
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

    public override string ToString() => Value;
}

public abstract record QuotaMutation<TSpec>;

public sealed record CreateQuotaMutation<TSpec>(TSpec Spec) : QuotaMutation<TSpec>;

public sealed record UpdateQuotaMutation<TSpec>(
    TSpec PreviousSpec,
    TSpec NextSpec) : QuotaMutation<TSpec>;

public sealed record DeleteQuotaMutation<TSpec>(TSpec Spec) : QuotaMutation<TSpec>;

public sealed record QuotaReservationLease(
    string ReservationId,
    string TenantId,
    QuotaDelta Delta,
    DateTime ExpiresAtUtc,
    QuotaResourceKey ResourceKey);

public interface IQuotaAdmissionService
{
    Task<Result<QuotaReservationLease>> ReserveAsync(
        string tenantId,
        QuotaDelta delta,
        QuotaResourceKey resourceKey,
        CancellationToken cancellationToken = default);

    Task<Result> CommitAsync(
        string reservationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result> CancelAsync(
        string reservationId,
        CancellationToken cancellationToken = default);

    Task<Result> ApplyUsageDeltaAsync(
        string tenantId,
        QuotaDelta delta,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

public interface IQuotaAdmissionExecutor
{
    Task<Result<TResponse>> ExecuteAsync<TSpec, TResponse>(
        string tenantId,
        QuotaResourceKey resourceKey,
        QuotaMutation<TSpec> mutation,
        Func<IClientSessionHandle, CancellationToken, Task<Result<TResponse>>> mutationCallback,
        CancellationToken cancellationToken = default)
        where TResponse : notnull;

    Task<Result<TResponse>> ExecuteAsync<TSpec, TPreparation, TResponse>(
        string tenantId,
        QuotaResourceKey resourceKey,
        QuotaMutation<TSpec> mutation,
        Func<CancellationToken, Task<Result<TPreparation>>> preparationCallback,
        Func<TPreparation, IClientSessionHandle, CancellationToken, Task<Result<TResponse>>> mutationCallback,
        CancellationToken cancellationToken = default)
        where TPreparation : notnull
        where TResponse : notnull;
}
