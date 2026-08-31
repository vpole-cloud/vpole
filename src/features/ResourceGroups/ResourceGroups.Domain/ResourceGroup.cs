using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.Shared.Models;

namespace Pole.Features.ResourceGroups.Domain;

public sealed record ResourceGroup(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string TenantId,
    string Name,
    string Description,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string DefaultIsolationScopeId,
    ResourceGroupLifecycleState Lifecycle,
    ImmutableList<ShortResourceInfo> Resources,
    ImmutableHashSet<string> Tags,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public ResourceGroup UpdateMetadata(
        string description,
        ImmutableHashSet<string> tags,
        DateTime updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var normalizedDescription = ResourceGroupValueGuard.Optional(
            description,
            nameof(description),
            ResourceGroupValueGuard.MaximumResourceGroupDescriptionLength);
        var normalizedTags = tags
            .Select(tag => ResourceGroupValueGuard.Required(
                tag,
                nameof(tags),
                ResourceGroupValueGuard.MaximumResourceGroupTagLength))
            .ToImmutableHashSet(StringComparer.Ordinal);
        if (string.Equals(Description, normalizedDescription, StringComparison.Ordinal) &&
            Tags.SetEquals(normalizedTags))
        {
            return this;
        }

        var normalizedUpdatedAtUtc = ResourceGroupValueGuard.UpdatedUtc(
            updatedAtUtc,
            UpdatedAtUtc,
            nameof(updatedAtUtc));
        return this with
        {
            Description = normalizedDescription,
            Tags = normalizedTags,
            Revision = checked(Revision + 1),
            UpdatedAtUtc = normalizedUpdatedAtUtc
        };
    }

    public ResourceGroup RequestDeletion(DateTime requestedAtUtc)
    {
        if (Lifecycle is DeletingResourceGroupLifecycleState)
            return this;

        var normalizedRequestedAtUtc = ResourceGroupValueGuard.UpdatedUtc(
            requestedAtUtc,
            UpdatedAtUtc,
            nameof(requestedAtUtc));
        return this with
        {
            Lifecycle = new DeletingResourceGroupLifecycleState(normalizedRequestedAtUtc),
            Revision = checked(Revision + 1),
            UpdatedAtUtc = normalizedRequestedAtUtc
        };
    }
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(ActiveResourceGroupLifecycleState),
    typeof(DeletingResourceGroupLifecycleState))]
public abstract record ResourceGroupLifecycleState;

[BsonDiscriminator("active")]
public sealed record ActiveResourceGroupLifecycleState : ResourceGroupLifecycleState;

[BsonDiscriminator("deleting")]
public sealed record DeletingResourceGroupLifecycleState(DateTime RequestedAtUtc)
    : ResourceGroupLifecycleState;

public static class ResourceGroupFactory
{
    public sealed record ResourceGroupCreateOptions(
        string Id,
        string DefaultIsolationScopeId,
        ImmutableList<ShortResourceInfo> Resources,
        ImmutableHashSet<string> Tags,
        DateTime CreatedAtUtc);
    
    public static ResourceGroupCreateOptions DefaultCreateOptions =>
        new(
            Id: ObjectId.GenerateNewId().ToString(),
            DefaultIsolationScopeId: ObjectId.GenerateNewId().ToString(),
            Resources: ImmutableList<ShortResourceInfo>.Empty,
            Tags: ImmutableHashSet<string>.Empty,
            CreatedAtUtc: DateTime.UtcNow);
    
    public static ResourceGroup Create(
        string tenantId,
        string name,
        string description,
        ResourceGroupCreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Resources);
        ArgumentNullException.ThrowIfNull(options.Tags);

        var createdAtUtc = ResourceGroupValueGuard.Utc(
            options.CreatedAtUtc,
            nameof(options.CreatedAtUtc));
        var tags = options.Tags
            .Select(tag => ResourceGroupValueGuard.Required(
                tag,
                nameof(options.Tags),
                ResourceGroupValueGuard.MaximumResourceGroupTagLength))
            .ToImmutableHashSet(StringComparer.Ordinal);

        return new ResourceGroup(
            ResourceGroupValueGuard.ObjectId(options.Id, nameof(options.Id)),
            ResourceGroupValueGuard.ObjectId(tenantId, nameof(tenantId)),
            ResourceGroupValueGuard.Required(
                name,
                nameof(name),
                ResourceGroupValueGuard.MaximumResourceGroupNameLength),
            ResourceGroupValueGuard.Optional(
                description,
                nameof(description),
                ResourceGroupValueGuard.MaximumResourceGroupDescriptionLength),
            ResourceGroupValueGuard.ObjectId(
                options.DefaultIsolationScopeId,
                nameof(options.DefaultIsolationScopeId)),
            new ActiveResourceGroupLifecycleState(),
            options.Resources,
            tags,
            Revision: 1,
            createdAtUtc,
            createdAtUtc);
    }
}
