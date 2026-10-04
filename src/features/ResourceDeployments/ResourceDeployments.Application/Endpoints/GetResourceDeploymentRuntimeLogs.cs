using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Application.Diagnostics;
using Pole.Features.ResourceDeployments.Application.Persistence.Reads;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Mediator;
using Pole.Validation;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Application.Endpoints;

public sealed record GetResourceDeploymentRuntimeLogsRequest(
    [FromRoute] string DeploymentId,
    [FromTenantHeader] string TenantId,
    [FromQuery] string InstanceUid,
    [FromQuery] string Container,
    [FromQuery] long ExpectedRevision,
    [FromQuery] bool Follow = false,
    [FromQuery] bool Previous = false,
    [FromQuery] int TailLines = 100) : IMediatorRequest<GetResourceDeploymentRuntimeLogsOutcome>, ITenantReadOperation;

public union GetResourceDeploymentRuntimeLogsOutcome(
    PreparedRuntimeLogSession,
    RuntimeLogsUnavailable,
    ResourceDeploymentNotFound);
public sealed class GetResourceDeploymentRuntimeLogsValidator : AbstractValidator<GetResourceDeploymentRuntimeLogsRequest>
{
    public GetResourceDeploymentRuntimeLogsValidator()
    {
        RuleFor(request => request.DeploymentId).MongoObjectId("Deployment ID");
        RuleFor(request => request.TenantId).MongoObjectId(TenantHeaders.TenantId);
        RuleFor(request => request.InstanceUid).NotEmpty().MaximumLength(128).Matches("^[a-zA-Z0-9-]+$");
        RuleFor(request => request.Container).NotEmpty().MaximumLength(63).Matches("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$");
        RuleFor(request => request.ExpectedRevision).GreaterThan(0);
        RuleFor(request => request.TailLines).InclusiveBetween(1, 500);
    }
}
public sealed class GetResourceDeploymentRuntimeLogsHandler(ResourceDeploymentOperationsPersistence persistence, ResourceDeploymentDiagnostics diagnostics)
    : IMediatorOutcomeHandler<GetResourceDeploymentRuntimeLogsRequest, GetResourceDeploymentRuntimeLogsOutcome>
{
    public async Task<GetResourceDeploymentRuntimeLogsOutcome> HandleAsync(GetResourceDeploymentRuntimeLogsRequest request, CancellationToken cancellationToken = default)
    {
        var deployment = await persistence.LoadAsync(request.TenantId, request.DeploymentId.ToLowerInvariant(), cancellationToken);
        if (deployment is null) return new ResourceDeploymentNotFound(request.DeploymentId);
        if (deployment.Revision != request.ExpectedRevision) return new RuntimeLogsUnavailable("Superseded");
        return (await diagnostics.OpenAsync(deployment, new RuntimeLogSelection(request.InstanceUid, request.Container, request.Previous, request.Follow, request.TailLines), cancellationToken)) switch
        {
            PreparedRuntimeLogs ready => ready.Session,
            RuntimeLogsUnavailable unavailable => unavailable
        };
    }
}
public static class GetResourceDeploymentRuntimeLogsEndpoint
{
    public static IEndpointRouteBuilder MapGetResourceDeploymentRuntimeLogsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{DeploymentId}/runtime-logs", async (IMediator mediator, ResourceDeploymentDiagnostics diagnostics, [AsParameters] GetResourceDeploymentRuntimeLogsRequest request, CancellationToken ct) =>
            (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
            {
                PreparedRuntimeLogSession session => new RuntimeLogStreamResult(session, diagnostics),
                RuntimeLogsUnavailable unavailable => Results.Conflict(unavailable),
                ResourceDeploymentNotFound => Results.NotFound()
            })).WithName("GetResourceDeploymentRuntimeLogs");
        return app;
    }
}
