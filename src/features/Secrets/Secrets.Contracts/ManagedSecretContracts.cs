using System.Collections.Immutable;
using MongoDB.Driver;

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

public abstract record ManagedSecretFailure;
public sealed record ManagedSecretUnavailable : ManagedSecretFailure;
public sealed record ManagedSecretChanged : ManagedSecretFailure;
public sealed record ManagedSecretReferenceRejected : ManagedSecretFailure;
public sealed record ManagedSecretEncryptionUnavailable : ManagedSecretFailure;
public sealed record ManagedSecretMutationSucceeded;
public sealed record ManagedSecretKey(string Value)
{
    public override string ToString() => "ManagedSecretKey([REDACTED])";
}

public union PrepareManagedSecretOutcome(
    PreparedManagedSecret,
    ManagedSecretEncryptionUnavailable);

public union ManagedSecretMutationOutcome(
    ManagedSecretMutationSucceeded,
    ManagedSecretUnavailable,
    ManagedSecretChanged,
    ManagedSecretReferenceRejected,
    ManagedSecretEncryptionUnavailable);

public union ReadManagedSecretOutcome(
    ManagedSecretValues,
    ManagedSecretUnavailable,
    ManagedSecretEncryptionUnavailable);

public union ReadManagedSecretKeyOutcome(
    ManagedSecretKey,
    ManagedSecretUnavailable,
    ManagedSecretEncryptionUnavailable);

public union EnsureManagedSecretProjectionOutcome(
    SecretProjectionReference,
    ManagedSecretUnavailable);

/// <summary>Prepares an encrypted version, without persistence or external side effects.</summary>
public delegate Task<PrepareManagedSecretOutcome> PrepareManagedSecret(
    PrepareManagedSecretRequest request,
    CancellationToken ct);

/// <summary>Joins the caller transaction after its resource registration and deployment are staged.</summary>
public delegate Task<ManagedSecretMutationOutcome> CreateBoundManagedSecret(
    CreateBoundManagedSecretRequest request,
    IClientSessionHandle session,
    CancellationToken ct);

/// <summary>Reads credentials only through their tenant-scoped owning resource, never an arbitrary Secret ID.</summary>
public delegate Task<ReadManagedSecretOutcome> ReadManagedSecret(
    string tenantId,
    ManagedSecretOwner owner,
    CancellationToken ct);

public delegate Task<ManagedSecretMutationOutcome> RotateManagedSecret(
    RotateManagedSecretRequest request,
    IClientSessionHandle session,
    CancellationToken ct);

/// <summary>Removes the owner; shared Secrets remain as ordinary tenant Secrets.</summary>
public delegate Task<ManagedSecretMutationOutcome> ReleaseManagedSecret(
    ReleaseManagedSecretRequest request,
    IClientSessionHandle session,
    CancellationToken ct);

public sealed record SecretProjectionReference(
    string SecretId,
    string KubernetesName,
    long AppliedVersion);

public delegate Task<EnsureManagedSecretProjectionOutcome> EnsureManagedSecretProjection(
    EnsureManagedSecretProjectionRequest request,
    CancellationToken ct);

/// <summary>Reads one provider-owned credential field without exposing the remaining values to the caller.</summary>
public delegate Task<ReadManagedSecretKeyOutcome> ReadManagedSecretKey(
    ReadManagedSecretKeyRequest request,
    CancellationToken ct);

public sealed record PrepareManagedSecretRequest(
    string TenantId,
    string SecretId,
    ManagedSecretOwner Owner,
    string Name,
    long Version,
    IReadOnlyDictionary<string, string> Values,
    ManagedSecretKubernetesType KubernetesType)
{
    public override string ToString() => $"PrepareManagedSecretRequest({SecretId}, {Version}, [REDACTED])";
}

public sealed record CreateBoundManagedSecretRequest(
    PreparedManagedSecret Prepared,
    string ResourceGroupId,
    string IsolationScopeId,
    string DeploymentId);

public sealed record RotateManagedSecretRequest(
    PreparedManagedSecret Prepared,
    long ExpectedVersion);

public sealed record ReleaseManagedSecretRequest(
    string TenantId,
    ManagedSecretOwner Owner);

public sealed record EnsureManagedSecretProjectionRequest(
    string TenantId,
    ManagedSecretOwner Owner,
    string DeploymentId);

public sealed record ReadManagedSecretKeyRequest(
    string TenantId,
    ManagedSecretOwner Owner,
    string Key);
