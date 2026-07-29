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
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Application.Mutations;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class UpdateWebService
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapUpdateWebServiceEndpoint()
        {
            app.MapPut("/{Id}", async (IMediator mediator, [AsParameters] UpdateWebServiceRequest request, CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(x => Results.Accepted(value: x));
                })
                .WithName("UpdateWebService")
                .Produces<UpdateWebServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public sealed record UpdateWebServiceRequest(
    [FromRoute] string Id,
    [FromBody] UpdateWebServiceRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default
) : IMediatorRequest<UpdateWebServiceResponse>, ITenantWriteOperation;

public sealed record UpdateWebServiceRequestBody(
    WebServiceSpecDto Spec,
    ImmutableHashSet<string> Tags
);

public sealed record UpdateWebServiceResponse(
    string Id,
    PlacementSummaryDto Placement);

public sealed class UpdateWebServiceHandler(
    IWebServiceDbContext dbContext,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    IResourceDeploymentMutationWriter deploymentWriter)
    : IMediatorHandler<UpdateWebServiceRequest, UpdateWebServiceResponse>
{
    public async Task<Result<UpdateWebServiceResponse>> HandleAsync(
        UpdateWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<WebService>.Filter.And(
            Builders<WebService>.Filter.ForTenant(request.TenantId),
            Builders<WebService>.Filter.Eq(ws => ws.Id, request.Id));

        var webService = await dbContext.WebServices
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);

        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Failures.Conflict("A deleting web service cannot be updated.");

        var nextSpec = request.Body.Spec.ToDomain();
        return await quotaAdmissionExecutor.ExecuteAsync<WebServiceSpec, UpdateWebServiceResponse>(
            webService.TenantId,
            WebServiceQuotaResourceKey.Create(webService.Id),
            new UpdateQuotaMutation<WebServiceSpec>(webService.Spec, nextSpec),
            async (session, transactionCancellationToken) =>
            {
                var updatedWebService = webService.UpdateDesiredState(
                    nextSpec,
                    request.Body.Tags);
                var changes = GetChanges(webService, updatedWebService);
                var deploymentEventCore = EventCore.Create(webService.TenantId);

                var replace = await dbContext.WebServices.ReplaceOneAsync(
                    session,
                    Builders<WebService>.Filter.And(
                        filter,
                        Builders<WebService>.Filter.Eq(
                            item => item.Revision,
                            webService.Revision)),
                    updatedWebService,
                    cancellationToken: transactionCancellationToken);
                if (replace.ModifiedCount != 1)
                {
                    return Failures.Conflict(
                        "The web service changed while the update was being accepted.");
                }

                var deploymentResult = await deploymentWriter.RequestApplyAsync(
                    new ResourceDeploymentMutationCommand(
                        webService.TenantId,
                        ResourceDeploymentResourceTypes.WebService,
                        webService.Id),
                    session,
                    transactionCancellationToken);
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
                    WebServiceReleaseAction.Update,
                    request.ActorUserId.Value,
                    request.ActorEmail.Value,
                    changes.WebServiceSpec.New,
                    deploymentEventCore.TraceId,
                    deploymentEventCore.CorrelationId,
                    deploymentEventCore.OccurredOn,
                    deploymentGeneration: deployment.DesiredGeneration);
                await WebServiceReleasePersistence.InsertCurrentAsync(
                    dbContext,
                    session,
                    release,
                    transactionCancellationToken);
                await dbContext.StoreEventAsync(
                    new WebServiceUpdatedEvent(
                        request.Id,
                        webService.Name,
                        webService.ResourceGroupId,
                        webService.DnsRecord,
                        changes)
                    {
                        EventCore = EventCore.Create(webService.TenantId)
                    },
                    session,
                    transactionCancellationToken);

                return new UpdateWebServiceResponse(
                    webService.Id,
                    deployment.ToPlacementSummaryDto());
            },
            cancellationToken);
    }

    private static WebServiceUpdatedEventChanges GetChanges(
        WebService webService,
        WebService updatedWebService) =>
        new(UpdateLog<WebServiceSpecSnapshot>.Of(
                webService.Spec.ToSnapshot(),
                updatedWebService.Spec.ToSnapshot()),
            UpdateLog<ImmutableHashSet<string>>.Of(webService.Tags,
                updatedWebService.Tags));
}

public sealed class UpdateWebServiceValidator : AbstractValidator<UpdateWebServiceRequest>
{
    public UpdateWebServiceValidator()
    {
        RuleFor(x => x.Id)
            .NotEmpty().WithMessage("Web service ID is required.");

        RuleFor(x => x.Body.Spec)
            .NotNull().WithMessage("Web service specification is required.")
            .SetValidator(new WebServiceSpecDtoValidator());
    }
}
