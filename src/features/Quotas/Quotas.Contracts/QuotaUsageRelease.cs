using MongoDB.Driver;

namespace Pole.Features.Quotas.Contracts;

public sealed record ReleaseQuotaUsageRequest<TSpec>(
    string TenantId,
    TSpec Spec
);

// The caller conditionally transitions its resource and records replay protection in this same transaction.
public interface IQuotaUsageRelease
{
    Task ReleaseAsync<TSpec>(
        ReleaseQuotaUsageRequest<TSpec> request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}
