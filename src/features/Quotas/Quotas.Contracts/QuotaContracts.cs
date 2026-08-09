using MongoDB.Driver;
using Pole.Features.Quotas.Primitives;
using Pole.ResultType;

namespace Pole.Features.Quotas.Contracts;

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
