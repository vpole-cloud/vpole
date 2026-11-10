using MongoDB.Driver;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Application.Usage;
using Pole.Features.Quotas.Contracts;

namespace Pole.Features.Quotas.Application.Services;

internal sealed class QuotaUsageRelease(
    QuotaCatalog catalog,
    ApplyQuotaUsageDelta apply
) : IQuotaUsageRelease
{
    public Task ReleaseAsync<TSpec>(
        ReleaseQuotaUsageRequest<TSpec> request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var delta = QuotaDelta.Difference(QuotaDelta.Empty, catalog.Calculate(request.Spec));
        return apply(request.TenantId, delta, session, cancellationToken);
    }
}
