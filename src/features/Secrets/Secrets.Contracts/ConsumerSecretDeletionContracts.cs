using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.Secrets.Contracts;

public sealed record EnsureConsumerSecretBindingRemovalRequest(
    string TenantId,
    ResourceTypeId ConsumerType,
    string ConsumerId);

public sealed record ConsumerSecretBindingsRemovable;

public sealed record ConsumerSecretBindingsRequireRemoval(int BindingCount);

public sealed record ConsumerSecretBindingDeletionChanged;

public union EnsureConsumerSecretBindingRemovalOutcome(
    ConsumerSecretBindingsRemovable,
    ConsumerSecretBindingsRequireRemoval,
    ConsumerSecretBindingDeletionChanged);

/// <summary>The caller must fence the consumer registration in this transaction before deleting the consumer.</summary>
public delegate Task<EnsureConsumerSecretBindingRemovalOutcome> EnsureConsumerSecretBindingRemoval(
    EnsureConsumerSecretBindingRemovalRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
