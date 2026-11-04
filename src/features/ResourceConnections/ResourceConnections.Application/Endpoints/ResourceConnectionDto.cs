using Pole.Features.ResourceConnections.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceConnections.Application.Kubernetes;

namespace Pole.Features.ResourceConnections.Application.Endpoints;

public sealed record ResourceConnectionTargetDto(
    string TargetId,
    string Phase,
    string Status,
    string Host,
    int Port,
    string? FailureCode);

public sealed record ResourceConnectionDto(
    string Id,
    string Name,
    string SourceResourceType,
    string SourceResourceId,
    string DestinationResourceType,
    string DestinationResourceId,
    string EndpointId,
    string Status,
    long Generation,
    IReadOnlyList<ResourceConnectionTargetDto> Targets,
    GitResourceStatus? GitOps);

internal static class ResourceConnectionDtoMapping
{
    public static ResourceConnectionDto ToDto(this ResourceConnection connection)
    {
        var targets = connection.Lifecycle switch
        {
            ActiveResourceConnection active => active.Targets
                .Select(target => ToTargetDto(target.Snapshot, "Apply", target.Status switch
                {
                    PendingConnectionApply => ("Pending", null),
                    AppliedConnectionApply => ("Applied", null),
                    FailedConnectionApply failed => ("Failed", failed.Reason)
                }))
                .Concat(active.RetiringTargets.Select(target => ToTargetDto(
                    target.Snapshot,
                    "Cleanup",
                    target.Status switch
                    {
                        PendingConnectionCleanup => ("Pending", null),
                        CompletedConnectionCleanup => ("Completed", null),
                        FailedConnectionCleanup failed => ("Failed", failed.Reason)
                    })))
                .ToArray(),
            DeletingResourceConnection deleting => deleting.Targets
                .Select(target => ToTargetDto(target.Snapshot, "Cleanup", target.Status switch
                {
                    PendingConnectionCleanup => ("Pending", null),
                    CompletedConnectionCleanup => ("Completed", null),
                    FailedConnectionCleanup failed => ("Failed", failed.Reason)
                }))
                .ToArray()
        };
        var status = connection.Lifecycle switch
        {
            DeletingResourceConnection => "Deleting",
            ActiveResourceConnection active when active.Targets.All(target =>
                target.Status is AppliedConnectionApply) &&
                active.RetiringTargets.All(target =>
                    target.Status is CompletedConnectionCleanup) => "Applied",
            ActiveResourceConnection active when active.Targets.Any(target =>
                target.Status is FailedConnectionApply) ||
                active.RetiringTargets.Any(target =>
                    target.Status is FailedConnectionCleanup) => "Failed",
            ActiveResourceConnection => "Pending"
        };
        return new ResourceConnectionDto(
            connection.Id,
            connection.Name,
            connection.SourceResourceTypeId,
            connection.SourceResourceId,
            connection.DestinationResourceTypeId,
            connection.DestinationResourceId,
            connection.EndpointId,
            status,
            connection.Generation,
            targets,
            null);
    }

    private static ResourceConnectionTargetDto ToTargetDto(
        ConnectionTargetSnapshot snapshot,
        string phase,
        (string Status, string? FailureCode) outcome) => new(
            snapshot.TargetId,
            phase,
            outcome.Status,
            ResourceConnectionServiceDns.Build(
                snapshot.DestinationServiceName,
                snapshot.DestinationNamespace),
            snapshot.DestinationServicePort,
            outcome.FailureCode);
}
