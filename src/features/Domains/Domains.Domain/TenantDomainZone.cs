using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Dns;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.Domains.Domain;

public enum TenantDomainZoneKind
{
    Generated,
    ManagedReservation
}

public static class TenantDomainZoneLabelPolicy
{
    // Generated zones share the same global DNS tree as tenant-managed reservations. Reserving
    // this prefix prevents a managed label from occupying a name the platform may generate.
    public const string GeneratedPrefix = "pole-generated-";

    public static bool IsManagedLabelAllowed(string? label) =>
        label is not null &&
        !label.Trim().StartsWith(GeneratedPrefix, StringComparison.OrdinalIgnoreCase);
}

public sealed record TenantDomainZone(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string TenantId,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string DeploymentTargetId,
    string Label,
    string Domain,
    DomainIdentity Identity,
    long Revision,
    long ReferenceFence,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc) : ITenantOwned
{
    [BsonIgnore]
    public TenantDomainZoneKind Kind => Identity switch
    {
        GeneratedDomainIdentity => TenantDomainZoneKind.Generated,
        NamedDomainIdentity => TenantDomainZoneKind.ManagedReservation
    };
}

public static class TenantDomainZoneFactory
{
    public sealed record CreateOptions(
        string Id,
        DateTime CreatedAtUtc);

    public static TenantDomainZone CreateGenerated(
        string tenantId,
        string deploymentTargetId,
        string label,
        DnsName publicBaseDomain,
        CreateOptions options) =>
        Create(
            tenantId,
            deploymentTargetId,
            label,
            publicBaseDomain,
            DomainIdentityFactory.Generated(),
            options);

    public static TenantDomainZone CreateManagedReservation(
        string tenantId,
        string deploymentTargetId,
        string label,
        DnsName publicBaseDomain,
        string technicalName,
        CreateOptions options)
    {
        if (!TenantDomainZoneLabelPolicy.IsManagedLabelAllowed(label))
        {
            throw new ArgumentException(
                $"Labels beginning with '{TenantDomainZoneLabelPolicy.GeneratedPrefix}' are reserved for platform-generated zones.",
                nameof(label));
        }

        return Create(
            tenantId,
            deploymentTargetId,
            label,
            publicBaseDomain,
            DomainIdentityFactory.Named(technicalName),
            options);
    }

    private static TenantDomainZone Create(
        string tenantId,
        string deploymentTargetId,
        string label,
        DnsName publicBaseDomain,
        DomainIdentity identity,
        CreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(publicBaseDomain);
        ArgumentNullException.ThrowIfNull(options);
        var normalizedLabel = DnsLabel.Create(label);
        var createdAtUtc = DomainValueGuard.Utc(options.CreatedAtUtc, nameof(options.CreatedAtUtc));
        return new TenantDomainZone(
            DomainValueGuard.ObjectId(options.Id, nameof(options.Id)),
            DomainValueGuard.ObjectId(tenantId, nameof(tenantId)),
            DomainValueGuard.ObjectId(deploymentTargetId, nameof(deploymentTargetId)),
            normalizedLabel.Value,
            DnsName.Combine(normalizedLabel, publicBaseDomain).Value,
            identity,
            Revision: 1,
            ReferenceFence: 0,
            createdAtUtc,
            createdAtUtc);
    }
}
