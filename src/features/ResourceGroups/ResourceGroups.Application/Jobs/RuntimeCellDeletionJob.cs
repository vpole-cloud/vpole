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
