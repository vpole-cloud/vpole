using Pole.Features.Shared.Models;
using Pole.ResultType;

namespace Pole.Features.DnsRecords.Contracts;

public interface IDomainNameReservationService
{
    Task<Result<DnsRecordShortInfo>> ReserveAsync(
        ReserveDomainNameRequest request,
        CancellationToken cancellationToken = default);

    Task<Result> CommitAsync(
        CommitDomainNameRequest request,
        CancellationToken cancellationToken = default);

    Task<Result> ReleaseAsync(
        ReleaseDomainNameRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ReserveDomainNameRequest(
    string TenantId,
    string DomainName,
    ResourceType ResourceType,
    string ResourceId,
    string ResourceName,
    string ResourceGroupId);

public sealed record CommitDomainNameRequest(
    string TenantId,
    string DnsRecordId,
    string ResourceId);

public sealed record ReleaseDomainNameRequest(
    string TenantId,
    string DnsRecordId);
