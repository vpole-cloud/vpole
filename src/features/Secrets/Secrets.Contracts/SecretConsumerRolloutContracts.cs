// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Workloads;

namespace Pole.Features.Secrets.Contracts;

public sealed record SecretConsumerDeploymentProof(
    string DeploymentId,
    long DeploymentRevision,
    long DesiredGeneration
);

public sealed record SecretConsumerRolloutPending;

public sealed record SecretConsumerRolloutConfirmed(
    IReadOnlyList<SecretConsumerDeploymentProof> Deployments
);

public union SecretConsumerRolloutOutcome(SecretConsumerRolloutPending, SecretConsumerRolloutConfirmed);

/// <summary>
///     The consumer owns native rollout verification. Confirmation must cover every placement
///     and exclude old or terminating pods, truncated observations and unavailable native state.
/// </summary>
public interface ISecretConsumerRolloutObserver
{
    ResourceTypeId ResourceType { get; }

    Task<SecretConsumerRolloutOutcome> ObserveAsync(
        string tenantId,
        string consumerId,
        CancellationToken ct);
}
