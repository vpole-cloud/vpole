using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Dns;
using Pole.Multitenancy.Primitives;
using Pole.Workloads;

namespace Pole.Features.Domains.Domain;

public enum HostnameBindingKind
{
    Generated,
    Custom,
}

public sealed record HostnameBinding(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string TenantId,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string TenantDomainZoneId,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string DeploymentTargetId,
    string ResourceType,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string ResourceId,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string ResourceDeploymentId,
    string RelativeName,
    string Hostname,
    DomainIdentity Identity,
    long ReferenceFence,
    DateTime CreatedAtUtc
) : ITenantOwned
{
    [BsonIgnore]
    public HostnameBindingKind Kind =>
        Identity switch
        {
            GeneratedDomainIdentity => HostnameBindingKind.Generated,
            NamedDomainIdentity => HostnameBindingKind.Custom,
        };
}

public static class HostnameBindingFactory
{
    public static HostnameBinding CreateGenerated(
        string tenantId,
        string tenantDomainZoneId,
        string deploymentTargetId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        string relativeName,
        DnsName zoneDomain,
        CreateOptions options) => Create(
        tenantId,
        tenantDomainZoneId,
        deploymentTargetId,
        resourceType,
        resourceId,
        resourceDeploymentId,
        relativeName,
        zoneDomain,
        DomainIdentityFactory.Generated(),
        options);

    public static HostnameBinding CreateCustom(
        string tenantId,
        string tenantDomainZoneId,
        string deploymentTargetId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        string relativeName,
        DnsName zoneDomain,
        string technicalName,
        CreateOptions options) => Create(
        tenantId,
        tenantDomainZoneId,
        deploymentTargetId,
        resourceType,
        resourceId,
        resourceDeploymentId,
        relativeName,
        zoneDomain,
        DomainIdentityFactory.Named(technicalName),
        options);

    private static HostnameBinding Create(
        string tenantId,
        string tenantDomainZoneId,
        string deploymentTargetId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        string relativeName,
        DnsName zoneDomain,
        DomainIdentity identity,
        CreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(zoneDomain);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(resourceType.Value))
            throw new ArgumentException("A resource type is required.", nameof(resourceType));
        var relative = RelativeDnsName.Create(relativeName);
        return new HostnameBinding(
            DomainValueGuard.ObjectId(options.Id, nameof(options.Id)),
            DomainValueGuard.ObjectId(tenantId, nameof(tenantId)),
            DomainValueGuard.ObjectId(tenantDomainZoneId, nameof(tenantDomainZoneId)),
            DomainValueGuard.ObjectId(deploymentTargetId, nameof(deploymentTargetId)),
            resourceType.Value,
            DomainValueGuard.ObjectId(resourceId, nameof(resourceId)),
            DomainValueGuard.ObjectId(resourceDeploymentId, nameof(resourceDeploymentId)),
            relative.Value,
            relative.Under(zoneDomain).Value,
            identity,
            ReferenceFence: 0,
            DomainValueGuard.Utc(options.CreatedAtUtc, nameof(options.CreatedAtUtc)));
    }

    public sealed record CreateOptions(
        string Id,
        DateTime CreatedAtUtc
    );
}

internal static class DomainValueGuard
{
    public static string ObjectId(string value, string parameterName) => MongoDB.Bson.ObjectId.TryParse(value, out _)
        ? value
        : throw new ArgumentException("A MongoDB ObjectId is required.", parameterName);

    public static DateTime Utc(DateTime value, string parameterName) => value.Kind == DateTimeKind.Utc
        ? value
        : throw new ArgumentException("A UTC timestamp is required.", parameterName);
}
