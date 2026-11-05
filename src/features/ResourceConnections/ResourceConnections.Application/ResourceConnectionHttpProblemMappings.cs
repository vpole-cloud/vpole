using Pole.ASP.Helpers;
using Pole.Features.ResourceConnections.Application.Endpoints;
using Pole.Features.ResourceConnections.Application.Common;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Application.References;
using Pole.Features.ResourceConnections.Application.Topology;

namespace Pole.Features.ResourceConnections.Application;

public static class ResourceConnectionHttpProblemMappings
{
    public static HttpProblem MapResourceMissing(ConnectionResourceMissing _) => Problem(
        404, "resource-connections.resource-not-found", "Resource not found");

    public static HttpProblem MapEndpointUnsupported(ConnectionEndpointUnsupported _) => Problem(
        422, "resource-connections.endpoint-unsupported", "Endpoint is not supported");

    public static HttpProblem MapSourceUnsupported(ConnectionSourceUnsupported _) => Problem(
        422, "resource-connections.source-unsupported", "Source is not supported");

    public static HttpProblem MapNoSourcePlacement(ConnectionSourceHasNoPlacements _) => Problem(
        422, "resource-connections.source-has-no-placement", "Source has no deployment");

    public static HttpProblem MapPlacementDeleting(ConnectionPlacementDeleting _) => Problem(
        409, "resource-connections.deployment-deleting", "A deployment is deleting");

    public static HttpProblem MapDestinationTargetMissing(ConnectionDestinationTargetMissing _) => Problem(
        422, "resource-connections.destination-target-missing", "Destination target is missing");

    public static HttpProblem MapDestinationTargetAmbiguous(ConnectionDestinationTargetAmbiguous _) => Problem(
        422, "resource-connections.destination-target-ambiguous", "Destination target is ambiguous");

    public static HttpProblem MapNameAlreadyExists(ConnectionNameAlreadyExists _) => Problem(
        409, "resource-connections.name-exists", "Connection name already exists");

    public static HttpProblem MapNotFound(ResourceConnectionNotFound _) => Problem(
        404, "resource-connections.not-found", "Connection not found");

    public static HttpProblem MapDeletionConflict(ResourceConnectionDeletionConflict _) => Problem(
        409, "resource-connections.deletion-conflict", "Connection deletion conflicted");

    public static HttpProblem MapGitOwned(ResourceConnectionGitOwned _) => Problem(
        409, "resource-connections.git-owned", "Connection configuration is managed by Git");

    private static HttpProblem Problem(int status, string code, string title) =>
        new(status, PoleProblemTypeId.FromCode(code), title, code);
}
