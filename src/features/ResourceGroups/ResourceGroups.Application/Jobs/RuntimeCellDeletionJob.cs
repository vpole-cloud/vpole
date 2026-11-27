// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.ResourceGroups.Application.RuntimeCells;
using Pole.Features.ResourceGroups.Events;

namespace Pole.Features.ResourceGroups.Application.Jobs;

public delegate void ScheduleRuntimeCellDeletionAt(RuntimeCellDeletionRequestedEvent request, DateTime notBeforeUtc);

public sealed class RuntimeCellDeletionJob(
    RuntimeCellDeletionProgress progress
)
{
    public Task ExecuteAsync(RuntimeCellDeletionRequestedEvent request, CancellationToken cancellationToken) =>
        progress.HandleAsync(request, cancellationToken);
}
