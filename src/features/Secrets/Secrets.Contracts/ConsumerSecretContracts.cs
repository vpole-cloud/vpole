using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.Secrets.Contracts;

public sealed record ConsumerSecretKeyReference(
    string SecretId,
    string Key
);

public sealed record ConsumerSecretReferencesAdmitted;

public enum ConsumerSecretReferenceError
{
    SecretBindingUnavailable,
    SecretValuesMissing,
    SecretKeyUnavailable,
    SecretDeploymentCoverageUnavailable,
    SecretConsumerUnavailable,
}

public sealed record ConsumerSecretReferenceUnavailable(
    string SecretId,
    ConsumerSecretReferenceError Reason
);

public static class ConsumerSecretReferenceErrorCodes
{
    public static string Code(ConsumerSecretReferenceError reason) => reason switch
    {
        ConsumerSecretReferenceError.SecretBindingUnavailable => "SecretBindingUnavailable",
        ConsumerSecretReferenceError.SecretValuesMissing => "SecretValuesMissing",
        ConsumerSecretReferenceError.SecretKeyUnavailable => "SecretKeyUnavailable",
        ConsumerSecretReferenceError.SecretDeploymentCoverageUnavailable => "SecretDeploymentCoverageUnavailable",
        ConsumerSecretReferenceError.SecretConsumerUnavailable => "SecretConsumerUnavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}

public sealed record ConsumerSecretProjectionPending(
    string SecretId
);

public sealed record ConsumerSecretProjectionKey(
    string SecretId,
    string SourceKey,
    string KubernetesName,
    string ProjectedKey,
    long AppliedVersion
);

public sealed record ConsumerSecretProjectionKeys(
    ImmutableArray<ConsumerSecretProjectionKey> Keys
);

public union AdmitConsumerSecretReferencesOutcome(ConsumerSecretReferencesAdmitted, ConsumerSecretReferenceUnavailable);

public union EnsureConsumerSecretProjectionsOutcome(
    ConsumerSecretProjectionKeys,
    ConsumerSecretReferenceUnavailable,
    ConsumerSecretProjectionPending);

public enum ConsumerSecretAvailabilityPolicy
{
    RequireAvailable,
    AllowUnavailableForCleanup,
}

public enum PreviousSecretReferenceRetention
{
    DoNotRetain,
    KeepUntilRolloutConfirmed,
}

/// <summary>
///     Authorizes logical keys for all desired consumer placements and pins keys against concurrent rotation. No
///     values leave Secrets.
/// </summary>
public delegate Task<AdmitConsumerSecretReferencesOutcome> AdmitConsumerSecretReferences(
    AdmitConsumerSecretReferencesRequest request,
    IClientSessionHandle session,
    CancellationToken ct);

/// <summary>
///     Returns native name/key references only after rechecking current binding authority and the exact projection
///     generation.
/// </summary>
public delegate Task<EnsureConsumerSecretProjectionsOutcome> EnsureConsumerSecretProjections(
    EnsureConsumerSecretProjectionsRequest request,
    CancellationToken ct);

public sealed record AdmitConsumerSecretReferencesRequest(
    string TenantId,
    string ResourceGroupId,
    string IsolationScopeId,
    ResourceTypeId ConsumerType,
    string ConsumerId,
    IReadOnlyCollection<ConsumerSecretKeyReference> PreviousReferences,
    IReadOnlyCollection<ConsumerSecretKeyReference> DesiredReferences,
    ConsumerSecretAvailabilityPolicy Availability,
    PreviousSecretReferenceRetention Retention
);

public sealed record EnsureConsumerSecretProjectionsRequest(
    string TenantId,
    ResourceTypeId ConsumerType,
    string ConsumerId,
    string DeploymentId,
    IReadOnlyCollection<ConsumerSecretKeyReference> References
);
