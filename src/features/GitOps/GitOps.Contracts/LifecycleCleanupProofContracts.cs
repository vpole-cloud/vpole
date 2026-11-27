// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.GitOps.Contracts;

public sealed record GitCleanupVersionTransition(
    string DocumentId,
    long Before,
    long After
);

public abstract record GitCleanupResource;

public sealed record GitRegisteredCleanupResource(
    ResourceTypeId ResourceType,
    string ResourceId
) : GitCleanupResource;

public sealed record GitDeclaredCleanupResource(
    string Kind,
    string ResourceId
) : GitCleanupResource;

public sealed record RecordGitLifecycleCleanupRequest(
    string TenantId,
    GitCleanupResource Resource,
    string CleanupId,
    IReadOnlyList<GitCleanupVersionTransition> Transitions
);

// Records causal evidence only. Owner cleanup may continue after its original run is paused or superseded.
public interface IGitLifecycleCleanupProofRecorder
{
    Task RecordAsync(
        RecordGitLifecycleCleanupRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}
