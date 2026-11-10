using Pole.Features.Events.Abstractions;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Events;

namespace Pole.Features.SourceBuilds.Application.Common;

internal static class SourceBuildJobEventExtensions
{
    public static SourceBuildJobStateChangedEvent ToStateChangedEvent(this SourceBuildJob job) => new(
        job.Id,
        job.WebServiceId,
        job.State switch
        {
            QueuedSourceBuild => "Queued",
            CloningSourceBuild => "Cloning",
            AnalyzingSourceBuild => "Analyzing",
            BuildingSourceBuild => "Building",
            PushingSourceBuild => "Pushing",
            DeployingSourceBuild => "Deploying",
            SucceededSourceBuild => "Succeeded",
            FailedSourceBuild => "Failed",
            _ => throw new InvalidOperationException("Unsupported source build state."),
        },
        job.UpdatedAtUtc)
    {
        EventCore = EventCore.Create(job.TenantId),
    };
}
