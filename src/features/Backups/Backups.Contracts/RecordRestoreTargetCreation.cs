// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;

namespace Pole.Features.Backups.Contracts;

public sealed record RecordRestoreTargetCreationRequest(
    string TenantId,
    string OperationId,
    string ArchiveId,
    string NewResourceId
);

public enum RestoreTargetCreationDecision
{
    Recorded,
    AlreadyRecorded,
    OperationUnavailable
}

/// <summary>
/// Records the single permitted creation of an operation's restore target. Call only inside the
/// transaction that creates the new resource and its credentials. Abort unless the result is Recorded.
/// </summary>
public delegate Task<RestoreTargetCreationDecision> RecordRestoreTargetCreation(
    RecordRestoreTargetCreationRequest request,
    IClientSessionHandle session,
    CancellationToken ct);
