using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Contracts;

public sealed record ResourceConnectionPlanningVersion(
    string ConnectionId,
    long Revision,
    long ReferenceFence
)
{
    public static string RevisionDocumentId(string connectionId) => $"ResourceConnection/{connectionId}";

    public static string ReferenceFenceDocumentId(string connectionId) => $"ResourceConnectionFence/{connectionId}";

    public static ResourceConnectionPlanningVersion? FromDocuments(
        string connectionId,
        IReadOnlyDictionary<string, long> documents) =>
        documents.TryGetValue(RevisionDocumentId(connectionId), out var revision) && revision > 0 &&
        documents.TryGetValue(ReferenceFenceDocumentId(connectionId), out var fence) && fence >= 0
            ? new ResourceConnectionPlanningVersion(connectionId, revision, fence)
            : null;
}

public sealed record ConnectionDestinationPlanningVersion(
    ResourceTypeId ResourceType,
    string ResourceId,
    string RegistrationId,
    long ReferenceFence
);

public sealed record ResourceConnectionReferencePlanningSnapshot(
    AdmitResourceConnectionReferencesRequest Request,
    IReadOnlyList<ResourceConnectionPlanningVersion> Connections,
    IReadOnlyList<ConnectionDestinationPlanningVersion> Destinations
);

public sealed record ResourceConnectionReferencePlanningFenced;

public sealed record ResourceConnectionReferencePlanningChanged;

public union LoadResourceConnectionReferencePlanningOutcome(
    ResourceConnectionReferencePlanningSnapshot,
    ResourceConnectionReferencesUnavailable);

public union FenceResourceConnectionReferencePlanningOutcome(
    ResourceConnectionReferencePlanningFenced,
    ResourceConnectionReferencePlanningChanged,
    ResourceConnectionReferencesUnavailable);

public delegate Task<LoadResourceConnectionReferencePlanningOutcome> LoadResourceConnectionReferencePlanning(
    AdmitResourceConnectionReferencesRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

public delegate Task<FenceResourceConnectionReferencePlanningOutcome> FenceResourceConnectionReferencePlanning(
    ResourceConnectionReferencePlanningSnapshot snapshot,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
