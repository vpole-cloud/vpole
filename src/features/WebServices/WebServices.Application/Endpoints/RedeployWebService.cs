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

public sealed record RedeployWebServiceResponse(string Id, int Revision, string ReleaseId);

public sealed class RedeployWebServiceHandler(IWebServiceDbContext dbContext)
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

        return await dbContext.RunInTransactionAsync<RedeployWebServiceResponse>(async (session, ct) =>
        {
            var nextRevision = Math.Max(webService.ActiveRevision, 1) + 1;
            var deploymentEventCore = EventCore.Create(webService.TenantId);
            var release = WebServiceReleaseFactory.CreatePending(
                webService.TenantId,
                webService.Id,
                nextRevision,
                WebServiceReleaseAction.Redeploy,
                request.ActorUserId.Value,
                request.ActorEmail.Value,
                webService.Spec.ToSnapshot(),
                deploymentEventCore.TraceId,
                deploymentEventCore.CorrelationId,
                deploymentEventCore.OccurredOn);

            await dbContext.WebServices.UpdateOneAsync(
                session,
                filter,
                Builders<WebService>.Update.Set(service => service.ActiveRevision, nextRevision),
                cancellationToken: ct);

            await dbContext.WebServiceReleases.InsertOneAsync(session, release, cancellationToken: ct);
            await dbContext.StoreEventAsync(
                release.ToDeploymentRequestedEvent(webService, deploymentEventCore),
                session,
                ct);

            return new RedeployWebServiceResponse(webService.Id, nextRevision, release.Id);
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
