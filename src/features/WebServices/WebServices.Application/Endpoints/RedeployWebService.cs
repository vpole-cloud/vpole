using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Application.Mutations;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class RedeployWebService
{
    public static IEndpointRouteBuilder MapRedeployWebServiceEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/redeploy", async (IMediator mediator, [AsParameters] RedeployWebServiceRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(x => Results.Accepted(value: x));
            })
            .WithName("RedeployWebService")
            .Produces<RedeployWebServiceResponse>(StatusCodes.Status202Accepted);

        return app;
    }
}

public sealed record RedeployWebServiceRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default
) : IMediatorRequest<RedeployWebServiceResponse>, ITenantWriteOperation;

public sealed record RedeployWebServiceResponse(
    string Id,
    int Revision,
    string ReleaseId,
    PlacementSummaryDto Placement);

public sealed class RedeployWebServiceHandler(
    IWebServiceDbContext dbContext,
    IResourceDeploymentMutationWriter deploymentWriter)
    : IMediatorHandler<RedeployWebServiceRequest, RedeployWebServiceResponse>
{
    public async Task<Result<RedeployWebServiceResponse>> HandleAsync(
        RedeployWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<WebService>.Filter.And(
            Builders<WebService>.Filter.ForTenant(request.TenantId),
            Builders<WebService>.Filter.Eq(service => service.Id, request.Id));

        var webService = await dbContext.WebServices
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken);

        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Failures.Conflict("A deleting web service cannot be redeployed.");

        return await dbContext.RunInTransactionAsync<RedeployWebServiceResponse>(async (session, ct) =>
        {
            var updatedWebService = webService.Redeploy();
            var deploymentEventCore = EventCore.Create(webService.TenantId);

            var replace = await dbContext.WebServices.ReplaceOneAsync(
                session,
                Builders<WebService>.Filter.And(
                    filter,
                    Builders<WebService>.Filter.Eq(
                        item => item.Revision,
                        webService.Revision)),
                updatedWebService,
                cancellationToken: ct);
            if (replace.ModifiedCount != 1)
            {
                return Failures.Conflict(
                    "The web service changed while redeployment was being accepted.");
            }

            var deploymentResult = await deploymentWriter.RequestApplyAsync(
                new ResourceDeploymentMutationCommand(
                    webService.TenantId,
                    ResourceDeploymentResourceTypes.WebService,
                    webService.Id),
                session,
                ct);
            if (!deploymentResult.TryGetValue(
                    out var deployment,
                    out var deploymentFailure))
            {
                return deploymentFailure;
            }

            var release = WebServiceReleaseFactory.CreatePending(
                webService.TenantId,
                webService.Id,
                updatedWebService.ActiveRevision,
                WebServiceReleaseAction.Redeploy,
                request.ActorUserId.Value,
                request.ActorEmail.Value,
                webService.Spec.ToSnapshot(),
                deploymentEventCore.TraceId,
                deploymentEventCore.CorrelationId,
                deploymentEventCore.OccurredOn,
                deploymentGeneration: deployment.DesiredGeneration);
            await WebServiceReleasePersistence.InsertCurrentAsync(
                dbContext,
                session,
                release,
                ct);

            return new RedeployWebServiceResponse(
                webService.Id,
                updatedWebService.ActiveRevision,
                release.Id,
                deployment.ToPlacementSummaryDto());
        }, cancellationToken: cancellationToken);
    }
}

public sealed class RedeployWebServiceValidator : AbstractValidator<RedeployWebServiceRequest>
{
    public RedeployWebServiceValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}
