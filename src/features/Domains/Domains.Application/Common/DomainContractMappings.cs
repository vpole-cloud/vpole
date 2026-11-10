using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.Common;

internal static class DomainContractMappings
{
    public static TenantDomainZoneSnapshot ToSnapshot(this TenantDomainZone zone) => new(
        zone.Id,
        zone.TenantId,
        zone.DeploymentTargetId,
        zone.Label,
        zone.Domain,
        zone.Kind switch
        {
            TenantDomainZoneKind.Generated => DomainZoneKinds.Generated,
            TenantDomainZoneKind.ManagedReservation => DomainZoneKinds.ManagedReservation,
            _ => throw new InvalidOperationException("The domain-zone kind is not supported."),
        },
        zone.Revision,
        zone.CreatedAtUtc,
        zone.UpdatedAtUtc,
        TechnicalName(zone.Identity));

    public static HostnameBindingSnapshot ToSnapshot(this HostnameBinding binding) => new(
        binding.Id,
        binding.TenantId,
        binding.TenantDomainZoneId,
        binding.DeploymentTargetId,
        binding.ResourceType,
        binding.ResourceId,
        binding.ResourceDeploymentId,
        binding.RelativeName,
        binding.Hostname,
        binding.Kind switch
        {
            HostnameBindingKind.Generated => HostnameBindingKinds.Generated,
            HostnameBindingKind.Custom => HostnameBindingKinds.Custom,
            _ => throw new InvalidOperationException("The hostname-binding kind is not supported."),
        },
        binding.CreatedAtUtc,
        TechnicalName(binding.Identity));

    private static string? TechnicalName(DomainIdentity identity) => identity switch
    {
        GeneratedDomainIdentity => null,
        NamedDomainIdentity named => named.TechnicalName,
    };
}
