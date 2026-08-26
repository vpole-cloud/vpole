using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Reads;
using Pole.Features.WebServices.Application.Persistence.Releases;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.WebServices.Application.Endpoints.Deployments;

public static class DeleteWebServiceDeployment
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapDeleteWebServiceDeploymentEndpoint()
        {
            app.MapDelete(
                    "/{Id}/deployments/{DeploymentId}",
                    async (
                        IMediator mediator,
                        IHttpProblemMapper httpProblems,
                        [AsParameters] DeleteWebServiceDeploymentRequest request,
                        CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken))
                        .ToHttpResult(outcome => outcome switch
                        {
                            PlacementSummaryDto value =>
                                Results.Accepted(value: value),
                            DeleteWebServiceDeploymentError error =>
                                httpProblems.Map(error).ToHttpResult()
                        }))
                .WithName("DeleteWebServiceDeployment")
                .Produces<PlacementSummaryDto>(StatusCodes.Status202Accepted);
            return app;
        }
    }
}

public sealed record DeleteWebServiceDeploymentRequest(
    [FromRoute] string Id,
    [FromRoute] string DeploymentId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<DeleteWebServiceDeploymentOutcome>, ITenantWriteOperation;

public sealed class DeleteWebServiceDeploymentHandler(
    LoadWebServiceById loadWebService,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    RequestExactResourceDeploymentDeletion requestExactResourceDeploymentDeletion,
    AdvanceResourceDeploymentRevision advanceResourceDeploymentRevision,
    PersistUpdatedWebServiceOutcome persistWebService,
    PersistCurrentWebServiceRelease persistRelease,
    IQuotaAdmissionExecutor quotaAdmission,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<
        DeleteWebServiceDeploymentRequest,
        DeleteWebServiceDeploymentOutcome>
{
    public async Task<DeleteWebServiceDeploymentOutcome> HandleAsync(
        DeleteWebServiceDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebService(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (webService is null)
            return Error(new DeleteWebServiceDeploymentStateError(
                new WebServiceNotFound(request.Id)));

        var deployments = await loadResourceDeploymentSet(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            request.Id,
            cancellationToken);
        var current = deployments.SingleOrDefault(deployment =>
            deployment.DeploymentId == request.DeploymentId);
        if (current is null)
        {
            return Error(new DeleteWebServiceDeploymentStateError(
                new WebServiceResourceDeploymentNotFound(
                    request.Id,
                    request.DeploymentId)));
        }
        if (string.Equals(
                current.DesiredOperation,
                "Delete",
                StringComparison.Ordinal))
        {
            return Error(new DeleteWebServiceDeploymentStateError(
                new WebServiceDeploymentAlreadyDeleting(
                    request.Id,
                    request.DeploymentId)));
        }

        var desiredDeployments = deployments.Where(deployment =>
            !string.Equals(
                deployment.DesiredOperation,
                "Delete",
                StringComparison.Ordinal)).ToArray();
        if (desiredDeployments.Length <= 1)
        {
            return Error(new DeleteWebServiceDeploymentStateError(
                new WebServiceLastDeploymentCannotBeRemoved(request.Id)));
        }

        var remaining = desiredDeployments.Where(deployment =>
            deployment.DeploymentId != request.DeploymentId).ToArray();
        var currentQuota = WebServiceDeploymentWorkflow.ToQuotaInput(
            desiredDeployments,
            webService.Spec);
        var nextQuota = WebServiceDeploymentWorkflow.ToQuotaInput(
            remaining,
            webService.Spec);
        return await quotaAdmission.ExecuteOutcomeAsync<
            WebServiceQuotaInput,
            DeleteWebServiceDeploymentOutcome>(
            webService.TenantId,
            WebServiceQuotaResourceKey.Create(webService.Id),
            new UpdateQuotaMutation<WebServiceQuotaInput>(currentQuota, nextQuota),
            quotaError => Error(quotaError),
            async (transaction, token) =>
            {
                var updated = webService.AdvancePlacementDesiredState();
                var deleted = await requestExactResourceDeploymentDeletion(
                    new ResourceDeploymentExactMutationRequest(
                        webService.TenantId,
                        WebServiceResourceDeployment.ResourceType,
                        webService.Id,
                        current.DeploymentId,
                        current.DeploymentRevision,
                        current.Compute.Profile,
                        ResourceRevision: updated.ActiveRevision),
                    transaction.Session,
                    token);
                if (!deleted.TryGetValue(out var result, out var failure))
                    return transaction.Abort(Error(
                        new DeleteWebServiceDeploymentError(failure)));

                var stamped = await advanceResourceDeploymentRevision(
                    new ResourceDeploymentResourceRevisionRequest(
                        webService.TenantId,
                        WebServiceResourceDeployment.ResourceType,
                        webService.Id,
                        updated.ActiveRevision),
                    transaction.Session,
                    token);
                if (!stamped.TryGetValue(out var snapshots, out var revisionFailure))
                    return transaction.Abort(Error(
                        new DeleteWebServiceDeploymentError(revisionFailure)));

                var retainedSnapshots = snapshots.Where(item =>
                    item.DeploymentId != current.DeploymentId).ToArray();
                var eventDeployments = WebServiceDeploymentWorkflow
                    .ToEventDeployments(retainedSnapshots)
                    .AddRange(WebServiceDeploymentWorkflow.ToEventDeployments(
                        [result],
                        WebServiceReleaseDeploymentOperation.Delete))
                    .OrderBy(item => item.ResourceDeploymentId, StringComparer.Ordinal)
                    .ToImmutableList();
                var saved = await persistWebService(
                    transaction.Session,
                    webService,
                    updated,
                    updated.ToUpdatedEvent(eventDeployments),
                    token);
                if (!saved.TryGetValue(out _, out var persistenceError))
                    return transaction.Abort(Error(
                        new DeleteWebServiceDeploymentError(persistenceError)));

                var releaseDeployments = WebServiceDeploymentWorkflow
                    .ToReleaseDeployments(retainedSnapshots)
                    .AddRange(WebServiceDeploymentWorkflow.ToReleaseDeployments(
                        [result],
                        WebServiceReleaseDeploymentOperation.Delete));
                var release = WebServiceReleaseFactory.CreatePending(
                    updated.TenantId,
                    updated.Id,
                    updated.ActiveRevision,
                    WebServiceReleaseAction.RemoveDeployment,
                    null,
                    null,
                    updated.Spec,
                    releaseDeployments,
                    null,
                    null,
                    new WebServiceReleaseFactory.CreateOptions(
                        ObjectId.GenerateNewId().ToString(),
                        timeProvider.GetUtcNow().UtcDateTime));
                await persistRelease(transaction.Session, release, token);
                return transaction.Commit(new DeleteWebServiceDeploymentOutcome(result));
            },
            cancellationToken);
    }

    private static DeleteWebServiceDeploymentOutcome Error(
        DeleteWebServiceDeploymentError error) =>
        new(error);
}

public sealed class DeleteWebServiceDeploymentValidator : AbstractValidator<DeleteWebServiceDeploymentRequest>
{
    public DeleteWebServiceDeploymentValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("Web service ID");
        RuleFor(x => x.DeploymentId).MongoObjectId("Resource deployment ID");
    }
}
