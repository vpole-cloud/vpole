using Pole.Features.Domains.Contracts;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.Domains.Application.Endpoints;

public sealed record DomainZoneDto(
    string Id,
    string TenantId,
    string DeploymentTargetId,
    string Label,
    string Domain,
    string Kind,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    string? TechnicalName,
    GitResourceStatus? GitOps)
{
    public static DomainZoneDto FromSnapshot(TenantDomainZoneSnapshot snapshot, GitResourceStatus? ownership) =>
        new(snapshot.Id, snapshot.TenantId, snapshot.DeploymentTargetId, snapshot.Label, snapshot.Domain,
            snapshot.Kind, snapshot.Revision, snapshot.CreatedAtUtc, snapshot.UpdatedAtUtc,
            snapshot.TechnicalName, ownership);
}
