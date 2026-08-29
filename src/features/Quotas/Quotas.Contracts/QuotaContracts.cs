using Pole.Features.Quotas.Primitives;

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

public sealed record QuotaExceeded(
    QuotaKey Quota,
    long Limit,
    long Requested,
    long Current);

public sealed record QuotaReservationConflict(
    string TenantId,
    QuotaResourceKey ResourceKey);

public union QuotaReservationError(
    QuotaExceeded,
    QuotaReservationConflict);

public sealed record QuotaReservationCommitted;

public sealed record QuotaReservationNotFound(string ReservationId);

public sealed record QuotaReservationCancelled(string ReservationId);

public sealed record QuotaReservationExpired(string ReservationId);

public union QuotaReservationCommitError(
    QuotaReservationNotFound,
    QuotaReservationCancelled,
    QuotaReservationExpired);

public union QuotaExecutionError(
    QuotaReservationError,
    QuotaReservationCommitError);

public interface IQuotaAdmissionExecutor
{
    Task<TOutcome> ExecuteOutcomeAsync<TSpec, TOutcome>(
        string tenantId,
        QuotaResourceKey resourceKey,
        QuotaMutation<TSpec> mutation,
        Func<QuotaExecutionError, TOutcome> quotaError,
        Func<
            QuotaAdmissionTransactionContext<TOutcome>,
            CancellationToken,
            Task<QuotaAdmissionTransactionDecision<TOutcome>>> mutationCallback,
        CancellationToken cancellationToken = default)
        where TOutcome : notnull;
}
