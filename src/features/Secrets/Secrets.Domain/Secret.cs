using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Encryption;
using Pole.Features.Secrets.Primitives;

namespace Pole.Features.Secrets.Domain;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(AllSecretDeployments), typeof(SelectedSecretDeployments))]
public abstract record SecretDeploymentSelection;

public sealed record AllSecretDeployments : SecretDeploymentSelection;

public sealed record SelectedSecretDeployments : SecretDeploymentSelection
{
    [BsonElement]
    public ImmutableArray<string> DeploymentIds { get; }

    [BsonConstructor]
    public SelectedSecretDeployments(ImmutableArray<string> deploymentIds)
    {
        if (deploymentIds.IsDefaultOrEmpty || deploymentIds.Length > 256 ||
            deploymentIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != deploymentIds.Length)
            throw new ArgumentException("Select between 1 and 256 distinct deployments.", nameof(deploymentIds));
        DeploymentIds = deploymentIds;
    }
}

public sealed record SecretConsumerIdentity(
    string ResourceType,
    string ResourceId);

public sealed record SecretKeyPins(
    ImmutableArray<string> Required,
    ImmutableArray<string> Retained)
{
    public static SecretKeyPins Empty { get; } = new([], []);

    [BsonIgnore]
    public IEnumerable<string> ProtectedKeys => Required.Concat(Retained).Distinct(StringComparer.Ordinal);

    public SecretKeyPins Require(IEnumerable<string> desiredKeys) => Pin(desiredKeys, []);

    public SecretKeyPins RequireThroughRollout(IEnumerable<string> desiredKeys) => Pin(desiredKeys, Required);

    private SecretKeyPins Pin(IEnumerable<string> desiredKeys, IEnumerable<string> previousKeys)
    {
        var desired = desiredKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        var retained = Retained.Concat(previousKeys)
            .Except(desired, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        return new SecretKeyPins(desired, retained);
    }
}

public sealed record SecretBinding(
    string Id,
    SecretConsumerIdentity Consumer,
    SecretDeploymentSelection Selection,
    ImmutableDictionary<string, string> KeyMappings,
    string? DeclarationName,
    SecretKeyPins Pins,
    [property: BsonRepresentation(BsonType.String)] SecretBindingUsage Usage)
{
    public SecretBinding RequireKeys(IEnumerable<string> desiredKeys) =>
        this with { Pins = Pins.Require(desiredKeys) };

    public SecretBinding RequireKeysThroughRollout(IEnumerable<string> desiredKeys) =>
        this with { Pins = Pins.RequireThroughRollout(desiredKeys) };

    public string ProjectedKey(string key) => KeyMappings.Count == 0 ? key : KeyMappings[key];
    public bool IncludesKey(string key) => KeyMappings.Count == 0 || KeyMappings.ContainsKey(key);

    public static SecretBinding Create(
        string id,
        string consumerType,
        string consumerId,
        SecretDeploymentSelection selection,
        IEnumerable<KeyValuePair<string, string>> keyMappings,
        SecretBindingUsage usage = SecretBindingUsage.Runtime) =>
        new(
            id,
            new SecretConsumerIdentity(consumerType, consumerId),
            selection,
            keyMappings.ToImmutableDictionary(),
            null,
            SecretKeyPins.Empty,
            Enum.IsDefined(usage) ? usage : throw new ArgumentOutOfRangeException(nameof(usage)));
}

public enum SecretLifecycle
{
    Active
}

// Ciphertext is deliberately stored separately from the metadata aggregate.
public sealed record SecretVersion(
    [property: BsonId]
    string Id,
    string TenantId,
    string SecretId,
    long Version,
    EncryptedValue Value,
    DateTime CreatedAtUtc)
{
    public override string ToString() => $"SecretVersion({SecretId}, {Version})";
}

public sealed record Secret(
    [property: BsonId] string Id,
    string TenantId,
    string ResourceGroupId,
    string IsolationScopeId,
    string Name,
    string NormalizedName,
    string Purpose,
    ImmutableArray<string> KeyNames,
    long CurrentVersion,
    long Revision,
    [property: BsonRepresentation(BsonType.String)] SecretLifecycle Lifecycle,
    ImmutableArray<SecretBinding> Bindings,
    SecretManagement Management,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    long ReferenceFence,
    bool DeclaredKeys,
    ImmutableArray<string> SuppliedKeys)
{
    public const int MaximumBindings = 256;
    public const long MetadataOnlyVersion = 0;
    public const long FirstValueVersion = 1;
    public bool HasValue(string key) =>
        CurrentVersion >= FirstValueVersion && (DeclaredKeys ? SuppliedKeys.Contains(key) : KeyNames.Contains(key));

    public UpdateDeclaredSecretOutcome UpdateDeclaredMetadata(string purpose, IEnumerable<string> keys, DateTime now)
    {
        var desired = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        if (Purpose == purpose && KeyNames.SequenceEqual(desired)) return this;
        if (Bindings.Any(binding => binding.Pins.ProtectedKeys.Concat(binding.KeyMappings.Keys).Except(desired).Any()))
            return new SecretDeclaredKeysInUse();
        return this with
        {
            Purpose = purpose,
            KeyNames = desired,
            DeclaredKeys = true,
            Revision = checked(Revision + 1),
            UpdatedAtUtc = now
        };
    }

    public DeclareSecretBindingOutcome DeclareBinding(SecretBinding binding, DateTime now)
    {
        var current = Bindings.SingleOrDefault(item => item.Id == binding.Id);
        if (current is not null && current.Usage != binding.Usage)
            return new SecretBindingUsageChanged();
        if (current is not null && (current.Consumer.ResourceType != binding.Consumer.ResourceType
            || current.Consumer.ResourceId != binding.Consumer.ResourceId))
            return new SecretBindingConsumerChanged(current.Consumer, binding.Consumer);
        if (current is null && Bindings.Any(item => item.Consumer.ResourceType == binding.Consumer.ResourceType
            && item.Consumer.ResourceId == binding.Consumer.ResourceId))
            return new SecretBindingConsumerConflict(binding.Consumer);
        var unknownKeys = binding.KeyMappings.Keys.Except(KeyNames).Order(StringComparer.Ordinal).ToImmutableArray();
        if (!unknownKeys.IsEmpty)
            return new UnknownSecretBindingKeys(unknownKeys);
        if (current is null && Bindings.Length >= MaximumBindings)
            return new SecretBindingCapacityExceeded(MaximumBindings);
        if (current is not null)
        {
            if (current.KeyMappings.OrderBy(pair => pair.Key).SequenceEqual(binding.KeyMappings.OrderBy(pair =>
                pair.Key))) return this;
            binding = binding with { Pins = current.Pins };
            var changedKeys = current.Pins.ProtectedKeys
                .Where(key => !binding.IncludesKey(key) || current.ProjectedKey(key) != binding.ProjectedKey(key))
                .Order(StringComparer.Ordinal)
                .ToImmutableArray();
            if (!changedKeys.IsEmpty)
                return new SecretBindingKeysInUse(changedKeys);
        }
        return this with
        {
            Bindings = current is null ? [.. Bindings, binding] :
            Bindings.Select(item => item.Id == current.Id ? binding : item).ToImmutableArray(),
            Revision = checked(Revision + 1),
            UpdatedAtUtc = now
        };
    }

    public Secret ReleaseRetainedKeys(string bindingId) => this with
    {
        Bindings = Bindings.Select(binding => binding.Id == bindingId
            ? binding with { Pins = binding.Pins with { Retained = [] } } : binding).ToImmutableArray(),
        ReferenceFence = checked(ReferenceFence + 1)
    };

    public Secret Rotate(
        IEnumerable<string> keys,
        DateTime now)
    {
        var ownedKeys = keys.ToImmutableArray();
        if (Bindings.Any(binding =>
            binding.KeyMappings.Keys.Concat(binding.Pins.ProtectedKeys).Except(ownedKeys).Any()))
            throw new InvalidOperationException("Rotation must retain keys used by bindings.");
        return this with
        {
            KeyNames = DeclaredKeys ? KeyNames : ownedKeys,
            SuppliedKeys = ownedKeys,
            CurrentVersion = checked(CurrentVersion + 1),
            Revision = checked(Revision + 1),
            UpdatedAtUtc = now
        };
    }

    public Secret Bind(
        SecretBinding binding,
        DateTime now)
    {
        if (Bindings.Length >= MaximumBindings)
            throw new InvalidOperationException("Secret binding limit reached.");
        if (Bindings.Any(x => x.Consumer.ResourceType == binding.Consumer.ResourceType
            && x.Consumer.ResourceId == binding.Consumer.ResourceId))
            throw new InvalidOperationException("Consumer already bound.");
        if (binding.KeyMappings.Keys.Except(KeyNames).Any())
            throw new InvalidOperationException("Binding references an unknown key.");
        return this with
        {
            Bindings = [.. Bindings, binding],
            Revision = checked(Revision + 1),
            UpdatedAtUtc = now
        };
    }

    public Secret Detach(
        string bindingId,
        DateTime now) => this with
        {
            Bindings = Bindings.Where(x => x.Id != bindingId).ToImmutableArray(),
            Management = Management is ProviderManagedSecret owner && owner.BindingId == bindingId
                ? new UnmanagedSecret() : Management,
            Revision = checked(Revision + 1),
            UpdatedAtUtc = now
        };
}

public static class SecretFactory
{
    public static Secret CreateMetadata(string id, string tenantId, string groupId, string scopeId,
        string name, string purpose, IEnumerable<string> keys, DateTime now) =>
        Create(id, tenantId, groupId, scopeId, name, purpose, keys, now) with
        { CurrentVersion = Secret.MetadataOnlyVersion, DeclaredKeys = true, SuppliedKeys = [] };

    public static Secret Create(
        string id,
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        string name,
        string purpose,
        IEnumerable<string> keys,
        DateTime now) =>
        CreateCore(id, tenantId, resourceGroupId, isolationScopeId, name, purpose, keys, new UnmanagedSecret(), now);

    public static Secret CreateManaged(
        string id,
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        string name,
        string purpose,
        IEnumerable<string> keys,
        SecretBinding ownerBinding,
        KubernetesSecretType kubernetesType,
        DateTime now) =>
        CreateCore(id, tenantId, resourceGroupId, isolationScopeId, name, purpose, keys,
            new ProviderManagedSecret(ownerBinding.Consumer.ResourceType, ownerBinding.Consumer.ResourceId,
                ownerBinding.Id, kubernetesType), now)
            .Bind(ownerBinding, now);

    private static Secret CreateCore(
        string id,
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        string name,
        string purpose,
        IEnumerable<string> keys,
        SecretManagement management,
        DateTime now) => new(
            id,
            tenantId,
            resourceGroupId,
            isolationScopeId,
            name.Trim(),
            name.Trim().ToLowerInvariant(),
            purpose.Trim(),
            keys.Order().ToImmutableArray(),
            Secret.FirstValueVersion,
            1,
            SecretLifecycle.Active,
            [],
            management,
            now,
            now,
            0,
            false,
            []);
}

public sealed record SecretDeclaredKeysInUse;
public union UpdateDeclaredSecretOutcome(
    Secret,
    SecretDeclaredKeysInUse);
public sealed record SecretBindingConsumerChanged(
    SecretConsumerIdentity Current,
    SecretConsumerIdentity Desired);
public sealed record SecretBindingUsageChanged;

public sealed record SecretBindingConsumerConflict(SecretConsumerIdentity Consumer);
public sealed record UnknownSecretBindingKeys(ImmutableArray<string> Keys);
public sealed record SecretBindingCapacityExceeded(int Maximum);
public sealed record SecretBindingKeysInUse(ImmutableArray<string> Keys);
public union DeclareSecretBindingOutcome(
    Secret,
    SecretBindingConsumerChanged,
    SecretBindingUsageChanged,
    SecretBindingConsumerConflict,
    UnknownSecretBindingKeys,
    SecretBindingCapacityExceeded,
    SecretBindingKeysInUse);
