// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.FeatureName.Domain;

public sealed record ResourceName(
    [property: BsonId, BsonRepresentation(BsonType.ObjectId)]
    string Id,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string TenantId,
    ResourceNameDetails Details,
    long Revision,
    ResourceNameAudit Audit
) : ITenantOwned
{
    public ResourceName UpdateDetails(ResourceNameDetails details, DateTime updatedAtUtc)
    {
        var normalized = ResourceNameDetails.Create(details.Name, details.Description);
        if (Details == normalized)
            return this;

        if (updatedAtUtc.Kind != DateTimeKind.Utc || updatedAtUtc < Audit.CreatedAtUtc)
            throw new ArgumentException("Update time must be UTC and follow creation.", nameof(updatedAtUtc));

        return this with
        {
            Details = normalized,
            Revision = checked(Revision + 1),
            Audit = Audit with { UpdatedAtUtc = updatedAtUtc }
        };
    }
}

public sealed record ResourceNameDetails(
    string Name,
    string Description
)
{
    public const int MaximumNameLength = 80;
    public const int MaximumDescriptionLength = 500;

    public static ResourceNameDetails Create(string name, string description)
    {
        var normalizedName = name.Trim();
        var normalizedDescription = description.Trim();
        if (normalizedName.Length is < 1 or > MaximumNameLength)
            throw new ArgumentException("Name must contain 1–80 characters.", nameof(name));

        if (normalizedDescription.Length > MaximumDescriptionLength)
            throw new ArgumentException("Description must contain at most 500 characters.", nameof(description));

        return new(normalizedName, normalizedDescription);
    }
}

public sealed record ResourceNameAudit(
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc
);

public static class ResourceNameFactory
{
    public sealed record CreateOptions(
        string Id,
        DateTime CreatedAtUtc
    );

    public static ResourceName Create(
        string tenantId,
        ResourceNameDetails details,
        CreateOptions options)
    {
        if (!ObjectId.TryParse(tenantId, out _))
            throw new ArgumentException("Tenant ID must be an ObjectId.", nameof(tenantId));

        if (!ObjectId.TryParse(options.Id, out _))
            throw new ArgumentException("Resource ID must be an ObjectId.", nameof(options));

        if (options.CreatedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Creation time must be UTC.", nameof(options));

        var normalized = ResourceNameDetails.Create(details.Name, details.Description);
        return new(options.Id, tenantId, normalized, 1, new(options.CreatedAtUtc, options.CreatedAtUtc));
    }
}
