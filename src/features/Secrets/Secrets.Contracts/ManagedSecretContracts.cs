using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.ResultType;

namespace Pole.Features.Secrets.Contracts;

public enum ManagedSecretKubernetesType
{
    Opaque,
    BasicAuthentication
}

public sealed record ManagedSecretOwner(
    string ResourceType,
    string ResourceId);

public sealed record PreparedManagedSecret(
    string SecretId,
    string TenantId,
    ManagedSecretOwner Owner,
    string Name,
    ManagedSecretKubernetesType KubernetesType,
    long Version,
    ImmutableArray<string> Keys,
    ImmutableArray<byte> EncryptedValues)
{
    public override string ToString() => $"PreparedManagedSecret({SecretId}, {Version})";
}

public sealed record ManagedSecretValues(
    string SecretId,
    string Name,
    ManagedSecretKubernetesType KubernetesType,
    long Version,
    ImmutableDictionary<string, string> Values)
{
    public override string ToString() => "ManagedSecretValues([REDACTED])";
}

public sealed record ManagedSecretUnavailable;
public sealed record ManagedSecretChanged;
public sealed record ManagedSecretReferenceRejected;
public sealed record ManagedSecretEncryptionUnavailable;

public union ManagedSecretError(
    ManagedSecretUnavailable,
    ManagedSecretChanged,
    ManagedSecretReferenceRejected,
    ManagedSecretEncryptionUnavailable);

/// <summary>Prepares an encrypted version, without persistence or external side effects.</summary>
public delegate Task<Result<PreparedManagedSecret, ManagedSecretEncryptionUnavailable>> PrepareManagedSecret(
    string tenantId,
    string secretId,
    ManagedSecretOwner owner,
    string name,
    long version,
    IReadOnlyDictionary<string, string> values,
    ManagedSecretKubernetesType kubernetesType,
    CancellationToken ct);

/// <summary>Joins the caller transaction after its resource registration and deployment are staged.</summary>
public delegate Task<Result<Unit, ManagedSecretError>> CreateBoundManagedSecret(
    PreparedManagedSecret prepared,
    string resourceGroupId,
    string isolationScopeId,
    string deploymentId,
    IClientSessionHandle session,
    CancellationToken ct);

/// <summary>Reads credentials only through their tenant-scoped owning resource, never an arbitrary Secret ID.</summary>
public delegate Task<Result<ManagedSecretValues, ManagedSecretError>> ReadManagedSecret(
    string tenantId,
    ManagedSecretOwner owner,
    CancellationToken ct);

public delegate Task<Result<Unit, ManagedSecretError>> RotateManagedSecret(
    PreparedManagedSecret prepared,
    long expectedVersion,
    IClientSessionHandle session,
    CancellationToken ct);

/// <summary>Removes the owner; shared Secrets remain as ordinary tenant Secrets.</summary>
public delegate Task<Result<Unit, ManagedSecretError>> ReleaseManagedSecret(
    string tenantId,
    ManagedSecretOwner owner,
    IClientSessionHandle session,
    CancellationToken ct);

public sealed record SecretProjectionReference(
    string SecretId,
    string KubernetesName,
    long AppliedVersion);

public delegate Task<Result<SecretProjectionReference, ManagedSecretUnavailable>> EnsureManagedSecretProjection(
    string tenantId,
    ManagedSecretOwner owner,
    string deploymentId,
    CancellationToken ct);

/// <summary>Reads one provider-owned credential field without exposing the remaining values to the caller.</summary>
public delegate Task<Result<string, ManagedSecretError>> ReadManagedSecretKey(
    string tenantId,
    ManagedSecretOwner owner,
    string key,
    CancellationToken ct);
