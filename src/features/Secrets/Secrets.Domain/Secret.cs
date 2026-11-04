using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Encryption;

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

public sealed record SecretBinding(
    string Id,
    string ConsumerType,
    string ConsumerId,
    SecretDeploymentSelection Selection,
    ImmutableDictionary<string, string> KeyMappings,
    string? DeclarationName,
    ImmutableArray<string> RequiredKeys,
    ImmutableArray<string> RetainedKeys)
{
    [BsonIgnore]
    public IEnumerable<string> ProtectedKeys => RequiredKeys.Concat(RetainedKeys).Distinct(StringComparer.Ordinal);

    public SecretBinding PinReferences(IEnumerable<string> desiredKeys, bool retainPreviousReferences)
    {
        var desired = desiredKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        var retained = RetainedKeys.Concat(retainPreviousReferences ? RequiredKeys : [])
            .Except(desired, StringComparer.Ordinal).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        return this with { RequiredKeys = desired, RetainedKeys = retained };
    }

    public string ProjectedKey(string key) => KeyMappings.Count == 0 ? key : KeyMappings[key];
    public bool IncludesKey(string key) => KeyMappings.Count == 0 || KeyMappings.ContainsKey(key);

    public static SecretBinding Create(
        string id,
        string consumerType,
        string consumerId,
        SecretDeploymentSelection selection,
        IEnumerable<KeyValuePair<string, string>> keyMappings) =>
        new(
            id,
            consumerType,
            consumerId,
            selection,
            keyMappings.ToImmutableDictionary(), null, [], []);
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
    public bool HasValue(string key) => CurrentVersion >= FirstValueVersion && (DeclaredKeys ? SuppliedKeys.Contains(key) : KeyNames.Contains(key));

    public UpdateDeclaredSecretOutcome UpdateDeclaredMetadata(string purpose, IEnumerable<string> keys, DateTime now)
    {
        var desired = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        if (Purpose == purpose && KeyNames.SequenceEqual(desired)) return this;
        if (Bindings.Any(binding => binding.ProtectedKeys.Concat(binding.KeyMappings.Keys).Except(desired).Any()))
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
        if (current is not null && (current.ConsumerType != binding.ConsumerType || current.ConsumerId != binding.ConsumerId))
            return new SecretBindingRejected(SecretBindingRejection.ImmutableConsumer);
        if (current is null && Bindings.Any(item => item.ConsumerType == binding.ConsumerType && item.ConsumerId == binding.ConsumerId))
            return new SecretBindingRejected(SecretBindingRejection.ConsumerAlreadyBound);
        if (binding.KeyMappings.Keys.Except(KeyNames).Any())
            return new SecretBindingRejected(SecretBindingRejection.UnknownKey);
        if (current is null && Bindings.Length >= MaximumBindings)
            return new SecretBindingRejected(SecretBindingRejection.LimitReached);
        if (current is not null)
        {
            if (current.KeyMappings.OrderBy(pair => pair.Key).SequenceEqual(binding.KeyMappings.OrderBy(pair => pair.Key))) return this;
            binding = binding with { RequiredKeys = current.RequiredKeys, RetainedKeys = current.RetainedKeys };
            if (current.ProtectedKeys.Any(key => !binding.IncludesKey(key) || current.ProjectedKey(key) != binding.ProjectedKey(key)))
                return new SecretBindingRejected(SecretBindingRejection.KeysInUse);
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
        Bindings = Bindings.Select(binding => binding.Id == bindingId ? binding with { RetainedKeys = [] } : binding).ToImmutableArray(),
        ReferenceFence = checked(ReferenceFence + 1)
    };

    public Secret Rotate(
        IEnumerable<string> keys,
        DateTime now)
    {
        var ownedKeys = keys.ToImmutableArray();
        if (Bindings.Any(binding => binding.KeyMappings.Keys.Concat(binding.ProtectedKeys).Except(ownedKeys).Any()))
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
        if (Bindings.Any(x => x.ConsumerType == binding.ConsumerType && x.ConsumerId == binding.ConsumerId))
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
            new ProviderManagedSecret(ownerBinding.ConsumerType, ownerBinding.ConsumerId, ownerBinding.Id, kubernetesType), now)
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
public enum SecretBindingRejection { ImmutableConsumer, ConsumerAlreadyBound, UnknownKey, LimitReached, KeysInUse }
public sealed record SecretBindingRejected(SecretBindingRejection Reason);
public union DeclareSecretBindingOutcome(
    Secret,
    SecretBindingRejected);
