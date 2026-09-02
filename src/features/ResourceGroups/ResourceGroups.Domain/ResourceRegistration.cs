using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Workloads;

namespace Pole.Features.ResourceGroups.Domain;

public sealed record ResourceRegistration(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string TenantId,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string ResourceGroupId,
    string ResourceTypeId,
    string ResourceId,
    string DisplayName,
    string? OwningPluginId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public static class ResourceRegistrationFactory
{
    public const string FirstPartyNamespace = "pole";
    public const int MaximumResourceIdLength = 512;
    public const int MaximumDisplayNameLength = 200;
    public const int MaximumPluginIdLength = 100;

    public sealed record CreateOptions(
        string Id,
        DateTime CreatedAtUtc);

    public static ResourceRegistration Create(
        string tenantId,
        string resourceGroupId,
        ResourceTypeId resourceTypeId,
        string resourceId,
        string displayName,
        string? owningPluginId,
        CreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalizedResourceTypeId = ResourceTypeId.Create(resourceTypeId.Value);
        var normalizedPluginId = NormalizePluginId(owningPluginId);
        var resourceTypeNamespace = normalizedResourceTypeId.Value.Split('/', 2)[0];
        if (resourceTypeNamespace == FirstPartyNamespace && normalizedPluginId is not null)
        {
            throw new ArgumentException(
                "First-party resource types cannot declare an owning plugin.",
                nameof(owningPluginId));
        }
        if (resourceTypeNamespace != FirstPartyNamespace &&
            !string.Equals(resourceTypeNamespace, normalizedPluginId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A third-party resource type namespace must match its owning plugin ID.",
                nameof(owningPluginId));
        }

        var createdAtUtc = ResourceGroupValueGuard.Utc(
            options.CreatedAtUtc,
            nameof(options.CreatedAtUtc));
        return new ResourceRegistration(
            ResourceGroupValueGuard.ObjectId(options.Id, nameof(options.Id)),
            ResourceGroupValueGuard.ObjectId(tenantId, nameof(tenantId)),
            ResourceGroupValueGuard.ObjectId(resourceGroupId, nameof(resourceGroupId)),
            normalizedResourceTypeId.Value,
            ResourceGroupValueGuard.Required(
                resourceId,
                nameof(resourceId),
                MaximumResourceIdLength),
            ResourceGroupValueGuard.Required(
                displayName,
                nameof(displayName),
                MaximumDisplayNameLength),
            normalizedPluginId,
            createdAtUtc,
            createdAtUtc);
    }

    public static ResourceRegistration UpdateDisplayName(
        ResourceRegistration registration,
        string displayName,
        DateTime updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var normalizedDisplayName = ResourceGroupValueGuard.Required(
            displayName,
            nameof(displayName),
            MaximumDisplayNameLength);
        if (string.Equals(registration.DisplayName, normalizedDisplayName, StringComparison.Ordinal))
            return registration;

        return registration with
        {
            DisplayName = normalizedDisplayName,
            UpdatedAtUtc = ResourceGroupValueGuard.UpdatedUtc(
                updatedAtUtc,
                registration.UpdatedAtUtc,
                nameof(updatedAtUtc))
        };
    }

    private static string? NormalizePluginId(string? owningPluginId)
    {
        if (owningPluginId is null)
            return null;

        var normalized = ResourceGroupValueGuard.Required(
            owningPluginId,
            nameof(owningPluginId),
            MaximumPluginIdLength);
        if (normalized.Contains('/') ||
            !ResourceTypeId.TryCreate($"{normalized}/resource", out _))
        {
            throw new ArgumentException(
                "Plugin ID must be a valid lowercase resource-type namespace.",
                nameof(owningPluginId));
        }

        return normalized;
    }
}
