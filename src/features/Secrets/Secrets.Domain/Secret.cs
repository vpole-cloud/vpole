using MongoDB.Bson.Serialization.Attributes;
using Pole.Encryption;

namespace Pole.Features.Secrets.Domain;

public sealed record SecretBinding(
    string Id,
    string ConsumerType,
    string ConsumerId,
    bool AllDeployments,
    string[] DeploymentIds,
    Dictionary<string, string> KeyMappings);

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
    string[] KeyNames,
    long CurrentVersion,
    long Revision,
    string Lifecycle,
    SecretBinding[] Bindings,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public const int MaximumBindings = 256;

    public Secret Rotate(
        string[] keys,
        DateTime now)
    {
        if (Bindings.Any(binding => binding.KeyMappings.Keys.Except(keys).Any()))
            throw new InvalidOperationException("Rotation must retain keys used by bindings.");
        return this with
        {
            KeyNames = keys,
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
        if (binding.AllDeployments == (binding.DeploymentIds.Length > 0))
            throw new InvalidOperationException("Select all deployments or explicit deployment IDs.");
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
        Bindings = Bindings.Where(x => x.Id != bindingId).ToArray(),
        Revision = checked(Revision + 1),
        UpdatedAtUtc = now
    };
}
