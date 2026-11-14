using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.Secrets.Contracts;

public sealed record ConsumerSecretAccessRequest(
    string TenantId,
    string ResourceGroupId,
    string IsolationScopeId,
    ResourceTypeId ConsumerType,
    string ConsumerId,
    string SecretId,
    ImmutableArray<string> Keys
);

public sealed record ConsumerSecretValues(
    ImmutableDictionary<string, string> Values
)
{
    public override string ToString() => "ConsumerSecretValues([REDACTED])";
}

public sealed record ConsumerSecretAccessDenied;

public sealed record ConsumerSecretAccessGranted;

public union GrantConsumerSecretAccessOutcome(ConsumerSecretAccessGranted, ConsumerSecretAccessDenied);

public union ReadConsumerSecretValuesOutcome(
    ConsumerSecretValues,
    ConsumerSecretAccessDenied,
    ManagedSecretEncryptionUnavailable);

/// <summary>Grants selected keys to a registered consumer in the caller-owned resource transaction.</summary>
public delegate Task<GrantConsumerSecretAccessOutcome> GrantConsumerSecretAccess(
    ConsumerSecretAccessRequest request,
    IClientSessionHandle session,
    CancellationToken ct);

/// <summary>Reads only the current keys authorized by the consumer's registered purpose and grant.</summary>
public delegate Task<ReadConsumerSecretValuesOutcome> ReadConsumerSecretValues(
    ConsumerSecretAccessRequest request,
    CancellationToken ct);
