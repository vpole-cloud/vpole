using System.Collections.Immutable;
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
using Pole.Features.WebServices.Events;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class RollbackWebService
{
    public static IEndpointRouteBuilder MapRollbackWebServiceEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/rollback", async (IMediator mediator, [AsParameters] RollbackWebServiceRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(x => Results.Accepted(value: x));
            })
            .WithName("RollbackWebService")
            .Produces<RollbackWebServiceResponse>(StatusCodes.Status202Accepted);

        return app;
    }
}

public sealed record RollbackWebServiceRequest(
    [FromRoute] string Id,
    [FromBody] RollbackWebServiceRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default
) : IMediatorRequest<RollbackWebServiceResponse>, ITenantWriteOperation;

public sealed record RollbackWebServiceRequestBody(int TargetRevision);

public sealed record RollbackWebServiceResponse(string Id, int Revision, string ReleaseId);

public sealed class RollbackWebServiceHandler(IWebServiceDbContext dbContext)
    : IMediatorHandler<RollbackWebServiceRequest, RollbackWebServiceResponse>
{
    public async Task<Result<RollbackWebServiceResponse>> HandleAsync(
        RollbackWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var webServiceFilter = Builders<WebService>.Filter.And(
            Builders<WebService>.Filter.ForTenant(request.TenantId),
            Builders<WebService>.Filter.Eq(service => service.Id, request.Id));

        var webService = await dbContext.WebServices
            .Find(webServiceFilter)
            .FirstOrDefaultAsync(cancellationToken);

        if (webService is null)
            return Result.NotFound($"Web service with ID '{request.Id}' not found.");

        var targetReleaseFilter = Builders<WebServiceRelease>.Filter.And(
            Builders<WebServiceRelease>.Filter.ForTenant(request.TenantId),
            Builders<WebServiceRelease>.Filter.Eq(release => release.WebServiceId, request.Id),
            Builders<WebServiceRelease>.Filter.Eq(release => release.Revision, request.Body.TargetRevision),
            Builders<WebServiceRelease>.Filter.Eq(release => release.Outcome, WebServiceReleaseOutcome.Succeeded));

        var targetRelease = await dbContext.WebServiceReleases
            .Find(targetReleaseFilter)
            .FirstOrDefaultAsync(cancellationToken);

        if (targetRelease is null)
            return Result.NotFound($"Succeeded release revision '{request.Body.TargetRevision}' was not found for web service '{request.Id}'.");

        return await dbContext.RunInTransactionAsync<RollbackWebServiceResponse>(async (session, ct) =>
        {
            var nextRevision = Math.Max(webService.ActiveRevision, 1) + 1;
            var deploymentEventCore = EventCore.Create(webService.TenantId);
            var release = WebServiceReleaseFactory.CreatePending(
                webService.TenantId,
                webService.Id,
                nextRevision,
                WebServiceReleaseAction.Rollback,
                request.ActorUserId.Value,
                request.ActorEmail.Value,
                targetRelease.Spec,
                deploymentEventCore.TraceId,
                deploymentEventCore.CorrelationId,
                deploymentEventCore.OccurredOn);

            var changes = new WebServiceUpdatedEventChanges(
                UpdateLog<WebServiceSpecSnapshot>.Of(webService.Spec.ToSnapshot(), targetRelease.Spec),
                UpdateLog<ImmutableHashSet<string>>.Of(webService.Tags, webService.Tags));

            await dbContext.WebServices.UpdateOneAsync(
                session,
                webServiceFilter,
                Builders<WebService>.Update
                    .Set(service => service.Spec, targetRelease.Spec.ToDomain())
                    .Set(service => service.ActiveRevision, nextRevision),
                cancellationToken: ct);

            await dbContext.WebServiceReleases.InsertOneAsync(session, release, cancellationToken: ct);
            await dbContext.StoreEventAsync(
                [
                    new WebServiceUpdatedEvent(
                        webService.Id,
                        webService.Name,
                        webService.ResourceGroupId,
                        webService.DnsRecord,
                        changes)
                    {
                        EventCore = EventCore.Create(webService.TenantId)
                    },
                    release.ToDeploymentRequestedEvent(webService, deploymentEventCore)
                ],
                session,
                ct);

            return new RollbackWebServiceResponse(webService.Id, nextRevision, release.Id);
        }, cancellationToken: cancellationToken);
    }
}

public sealed class RollbackWebServiceValidator : AbstractValidator<RollbackWebServiceRequest>
{
    public RollbackWebServiceValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Body.TargetRevision).GreaterThan(0);
    }
}
