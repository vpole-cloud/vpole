using System.Collections.Immutable;
using MongoDB.Driver;

namespace Pole.Features.Postgres.Contracts;

public sealed record PostgresManagedDatabaseOwner(
    string ResourceType,
    string ResourceId
);

public sealed record PostgresManagedDatabasePlacement(
    string ResourceGroupId,
    string IsolationScopeId,
    string DeploymentTargetId
);

public sealed record PostgresManagedDatabaseReference(
    string ClaimId,
    string PostgresClusterId,
    string Database,
    string Username
);

public sealed record PostgresManagedDatabaseRuntime(
    string Host,
    int Port,
    string KubernetesNamespace,
    string DeploymentTargetId,
    string RuntimeCellId,
    string ResourceDeploymentId,
    ImmutableDictionary<string, string> PodLabels
);

public sealed record PostgresManagedDatabaseCredential(
    string Username,
    string Password
)
{
    public override string ToString() => "PostgresManagedDatabaseCredential([REDACTED])";
}

public sealed record PostgresManagedDatabaseReady(
    PostgresManagedDatabaseReference Reference,
    PostgresManagedDatabaseRuntime Runtime,
    PostgresManagedDatabaseCredential Credential
)
{
    public override string ToString() => $"PostgresManagedDatabaseReady({Reference.ClaimId}, [REDACTED])";
}

public sealed record PostgresManagedDatabaseUnavailable;

public sealed record PostgresManagedDatabaseChanged;

public sealed record PostgresManagedDatabaseProtectionUnavailable;

public sealed record PostgresManagedDatabasePurged;

public enum PostgresManagedDatabaseIneligibility
{
    SynchronousHaRequired,
    DeploymentTargetMismatch,
    ConsumerPlacementUnavailable,
}

public sealed record PostgresManagedDatabaseIneligible(
    PostgresManagedDatabaseIneligibility Reason
);

public enum PostgresManagedDatabasePendingReason
{
    PlacementUnavailable,
    CredentialProjectionPending,
    NativeOwnershipConflict,
    DatabasePending,
    RolePending,
    PurgePending,
}

public sealed record PostgresManagedDatabasePending(
    PostgresManagedDatabasePendingReason Reason
);

public sealed record ClaimPostgresManagedDatabaseRequest(
    string TenantId,
    string PostgresClusterId,
    PostgresManagedDatabaseOwner Owner,
    PostgresManagedDatabasePlacement Placement,
    string SourceComponent
);

public union ClaimPostgresManagedDatabaseOutcome(
    PostgresManagedDatabaseReference,
    PostgresManagedDatabaseUnavailable,
    PostgresManagedDatabaseIneligible,
    PostgresManagedDatabaseChanged,
    PostgresManagedDatabaseProtectionUnavailable);

/// <summary>Atomically claims one dedicated database and least-privilege role in the consumer create transaction.</summary>
public delegate Task<ClaimPostgresManagedDatabaseOutcome> ClaimPostgresManagedDatabase(
    ClaimPostgresManagedDatabaseRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

public sealed record EnsurePostgresManagedDatabaseRequest(
    string TenantId,
    PostgresManagedDatabaseOwner Owner
);

public union EnsurePostgresManagedDatabaseOutcome(
    PostgresManagedDatabaseReady,
    PostgresManagedDatabasePending,
    PostgresManagedDatabaseUnavailable,
    PostgresManagedDatabaseProtectionUnavailable);

/// <summary>Reconciles only this owner's credentials, native database/role and exact workload network grant.</summary>
public delegate Task<EnsurePostgresManagedDatabaseOutcome> EnsurePostgresManagedDatabase(
    EnsurePostgresManagedDatabaseRequest request,
    CancellationToken cancellationToken);

public sealed record PurgePostgresManagedDatabaseRequest(
    string TenantId,
    PostgresManagedDatabaseOwner Owner
);

public union PurgePostgresManagedDatabaseOutcome(
    PostgresManagedDatabasePurged,
    PostgresManagedDatabasePending,
    PostgresManagedDatabaseUnavailable,
    PostgresManagedDatabaseChanged);

/// <summary>Final-purge operation: drops only this owner's database/role before releasing its claim and credential.</summary>
public delegate Task<PurgePostgresManagedDatabaseOutcome> PurgePostgresManagedDatabase(
    PurgePostgresManagedDatabaseRequest request,
    CancellationToken cancellationToken);

public sealed record PostgresManagedDatabaseCandidate(
    string Id,
    string Name,
    string DeploymentTargetId,
    string ResourceGroupId,
    string IsolationScopeId,
    int Instances,
    string ReplicationMode
);
