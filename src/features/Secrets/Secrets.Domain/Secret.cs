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
    ImmutableDictionary<string, string> KeyMappings)
{
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
            keyMappings.ToImmutableDictionary());
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
    [property: BsonId]
    string Id,
    string TenantId,
    string ResourceGroupId,
    string IsolationScopeId,
    string Name,
    string NormalizedName,
    string Purpose,
    ImmutableArray<string> KeyNames,
    long CurrentVersion,
    long Revision,
    [property: BsonRepresentation(BsonType.String)]
    SecretLifecycle Lifecycle,
    ImmutableArray<SecretBinding> Bindings,
    SecretManagement Management,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public const int MaximumBindings = 256;

    public Secret Rotate(
        IEnumerable<string> keys,
        DateTime now)
    {
        var ownedKeys = keys.ToImmutableArray();
        if (Bindings.Any(binding => binding.KeyMappings.Keys.Except(ownedKeys).Any()))
            throw new InvalidOperationException("Rotation must retain keys used by bindings.");
        return this with
        {
            KeyNames = ownedKeys,
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
            1,
            1,
            SecretLifecycle.Active,
            [],
            management,
            now,
            now);
}
