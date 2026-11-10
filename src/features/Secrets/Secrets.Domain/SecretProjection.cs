using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.Secrets.Domain;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(PendingSecretProjection),
    typeof(ReadySecretProjection),
    typeof(FailedSecretProjection),
    typeof(DeletedSecretProjection))]
public abstract record SecretProjectionState;

public sealed record PendingSecretProjection : SecretProjectionState;

public sealed record ReadySecretProjection : SecretProjectionState;

public sealed record FailedSecretProjection(
    string FailureCode
) : SecretProjectionState;

public sealed record DeletedSecretProjection : SecretProjectionState;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(UnclaimedSecretProjection), typeof(ClaimedSecretProjection))]
public abstract record SecretProjectionLease;

public sealed record UnclaimedSecretProjection : SecretProjectionLease;

public sealed record ClaimedSecretProjection(
    string ClaimId,
    DateTime ExpiresAtUtc
) : SecretProjectionLease;

public sealed record SecretProjection(
    [property: BsonId] string Id,
    string TenantId,
    string SecretId,
    string BindingId,
    string ResourceGroupId,
    string IsolationScopeId,
    string ConsumerType,
    string ConsumerId,
    string DeploymentId,
    string RuntimeCellId,
    string DeploymentTargetId,
    string KubernetesNamespace,
    string KubernetesName,
    [property: BsonRepresentation(BsonType.String)]
    KubernetesSecretType KubernetesType,
    long DesiredVersion,
    long DesiredRevision,
    long? AppliedVersion,
    SecretProjectionState State,
    DateTime? LastAttemptAtUtc,
    SecretProjectionLease Lease,
    long StorageRevision
) : ITenantOwned
{
    public SecretProjection Claim(string claimId, DateTime now) => this with
    {
        Lease = new ClaimedSecretProjection(claimId, now.AddMinutes(2)),
        LastAttemptAtUtc = now,
        StorageRevision = checked(StorageRevision + 1),
    };

    public SecretProjection Applied() => this with
    {
        AppliedVersion = DesiredVersion,
        State = new ReadySecretProjection(),
    };

    public SecretProjection Failed(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return this with { State = new FailedSecretProjection(code) };
    }

    public SecretProjection Deleted() => this with
    {
        AppliedVersion = null,
        State = new DeletedSecretProjection(),
    };
}

public static class SecretProjectionFactory
{
    public static SecretProjection Create(
        Secret secret,
        SecretBinding binding,
        string deploymentId,
        string runtimeCellId,
        string deploymentTargetId,
        string kubernetesNamespace) => new(
        $"{secret.TenantId}:{secret.Id}:{deploymentId}",
        secret.TenantId,
        secret.Id,
        binding.Id,
        secret.ResourceGroupId,
        secret.IsolationScopeId,
        binding.Consumer.ResourceType,
        binding.Consumer.ResourceId,
        deploymentId,
        runtimeCellId,
        deploymentTargetId,
        kubernetesNamespace,
        $"pole-secret-{secret.Id}-{deploymentId}",
        secret.Management is ProviderManagedSecret owner && owner.BindingId == binding.Id
            ? owner.KubernetesType
            : KubernetesSecretType.Opaque,
        secret.CurrentVersion,
        secret.Revision,
        null,
        new PendingSecretProjection(),
        null,
        new UnclaimedSecretProjection(),
        1);
}
