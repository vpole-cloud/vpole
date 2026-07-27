using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.ResourceGroups.Domain;

public sealed record IsolationScope(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string TenantId,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string ResourceGroupId,
    string Name,
    string NormalizedName,
    bool IsDefault,
    IsolationScopeLifecycleState Lifecycle,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public IsolationScope RequestDeletion(DateTime requestedAtUtc)
    {
        if (IsDefault)
            throw new InvalidOperationException("The default isolation scope cannot be deleted separately.");

        return RequestDeletionCore(requestedAtUtc);
    }

    public IsolationScope RequestResourceGroupDeletion(DateTime requestedAtUtc) =>
        RequestDeletionCore(requestedAtUtc);

    private IsolationScope RequestDeletionCore(DateTime requestedAtUtc)
    {
        if (Lifecycle is DeletingIsolationScopeLifecycleState)
            return this;

        var normalizedRequestedAtUtc = ResourceGroupValueGuard.UpdatedUtc(
            requestedAtUtc,
            UpdatedAtUtc,
            nameof(requestedAtUtc));
        return this with
        {
            Lifecycle = new DeletingIsolationScopeLifecycleState(normalizedRequestedAtUtc),
            Revision = checked(Revision + 1),
            UpdatedAtUtc = normalizedRequestedAtUtc
        };
    }
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(ActiveIsolationScopeLifecycleState),
    typeof(DeletingIsolationScopeLifecycleState))]
public abstract record IsolationScopeLifecycleState;

[BsonDiscriminator("active")]
public sealed record ActiveIsolationScopeLifecycleState : IsolationScopeLifecycleState;

[BsonDiscriminator("deleting")]
public sealed record DeletingIsolationScopeLifecycleState(DateTime RequestedAtUtc)
    : IsolationScopeLifecycleState;

public static class IsolationScopeFactory
{
    public const string DefaultScopeName = "default";
    public const int MaximumNameLength = ResourceGroupValueGuard.MaximumIsolationScopeNameLength;

    public sealed record CreateOptions(string Id, DateTime CreatedAtUtc);

    public static CreateOptions DefaultOptions =>
        new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow);

    public static IsolationScope CreateDefault(
        string tenantId,
        string resourceGroupId,
        CreateOptions options) =>
        Create(tenantId, resourceGroupId, DefaultScopeName, isDefault: true, options);

    public static IsolationScope CreateAdditional(
        string tenantId,
        string resourceGroupId,
        string name,
        CreateOptions options) =>
        Create(tenantId, resourceGroupId, name, isDefault: false, options);

    public static string NormalizeName(string name) =>
        ResourceGroupValueGuard.Required(
                name,
                nameof(name),
                ResourceGroupValueGuard.MaximumIsolationScopeNameLength)
            .ToLowerInvariant();

    private static IsolationScope Create(
        string tenantId,
        string resourceGroupId,
        string name,
        bool isDefault,
        CreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var normalizedName = NormalizeName(name);
        if (isDefault && !string.Equals(normalizedName, DefaultScopeName, StringComparison.Ordinal))
            throw new ArgumentException("The default isolation scope must use the system default name.", nameof(name));
        if (!isDefault && string.Equals(normalizedName, DefaultScopeName, StringComparison.Ordinal))
            throw new ArgumentException("The default isolation scope name is reserved.", nameof(name));

        var createdAtUtc = ResourceGroupValueGuard.Utc(options.CreatedAtUtc, nameof(options.CreatedAtUtc));
        return new IsolationScope(
            ResourceGroupValueGuard.ObjectId(options.Id, nameof(options.Id)),
            ResourceGroupValueGuard.ObjectId(tenantId, nameof(tenantId)),
            ResourceGroupValueGuard.ObjectId(resourceGroupId, nameof(resourceGroupId)),
            ResourceGroupValueGuard.Required(
                name,
                nameof(name),
                ResourceGroupValueGuard.MaximumIsolationScopeNameLength),
            normalizedName,
            isDefault,
            new ActiveIsolationScopeLifecycleState(),
            Revision: 1,
            createdAtUtc,
            createdAtUtc);
    }
}
