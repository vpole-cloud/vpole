// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Domains.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Queries;
using Pole.Features.WebServices.Application.Releases;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.WebServices.Application.Common.WebServiceHttpMappings;

namespace Pole.Features.WebServices.Application.Endpoints.Deployments.Hostnames;

public static class DeleteWebServiceDeploymentHostname
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapDeleteWebServiceDeploymentHostnameEndpoint()
        {
            app.MapDelete(
                    "/{Id}/deployments/{DeploymentId}/hostnames/{BindingId}",
                    async (
                            IMediator mediator, [AsParameters] DeleteWebServiceDeploymentHostnameRequest request,
                            CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
                        {
                            PlacementSummaryDto value => Results.Accepted(value: value),
                            WebServiceDeploymentHostnameError error => MapError(error),
                        }))
                .WithName("DeleteWebServiceDeploymentHostname")
                .Produces<PlacementSummaryDto>(StatusCodes.Status202Accepted);
            return app;
        }
    }
}

public sealed record DeleteWebServiceDeploymentHostnameRequest(
    [FromRoute] string Id,
    [FromRoute] string DeploymentId,
    [FromRoute] string BindingId,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<DeleteWebServiceDeploymentHostnameOutcome>, ITenantWriteOperation;

public union DeleteWebServiceDeploymentHostnameOutcome(PlacementSummaryDto, WebServiceDeploymentHostnameError);

public sealed class DeleteWebServiceDeploymentHostnameHandler(
    LoadWebServiceById loadWebService,
    LoadExactResourceDeployment loadExactResourceDeployment,
    DeleteCustomHostnameBinding deleteCustomHostnameBinding,
    LoadHostnameBindingsForDeploymentInTransaction loadHostnameBindingsForDeploymentInTransaction,
    RequestResourceDeploymentSetApply requestResourceDeploymentSetApply,
    AdvanceResourceDeploymentRevision advanceResourceDeploymentRevision,
    PersistUpdatedWebServiceOutcome persistWebService,
    PersistCurrentWebServiceRelease persistRelease,
    RunMongoOutcomeTransaction<DeleteWebServiceDeploymentHostnameOutcome> runTransaction,
    TimeProvider timeProvider
) : IMediatorOutcomeHandler<DeleteWebServiceDeploymentHostnameRequest, DeleteWebServiceDeploymentHostnameOutcome>
{
    public async Task<DeleteWebServiceDeploymentHostnameOutcome> HandleAsync(
        DeleteWebServiceDeploymentHostnameRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebService(request.TenantId, request.Id, cancellationToken);
        if (webService is null)
            return Error(new WebServiceDeploymentMutationStateError(new WebServiceNotFound(request.Id)));

        if (webService.Lifecycle is not (ActiveWebServiceLifecycle or AwaitingFirstArtifactWebServiceLifecycle))
            return Error(new WebServiceHostnameAttachmentUnavailable(webService.Id));

        var deployment = await loadExactResourceDeployment(
            new LoadExactResourceDeploymentRequest(
                TenantId: request.TenantId,
                ResourceType: WebServiceResourceDeployment.ResourceType,
                ResourceId: request.Id,
                DeploymentId: request.DeploymentId),
            cancellationToken: cancellationToken);
        if (deployment is null)
            return Error(
                new WebServiceDeploymentMutationStateError(
                    new WebServiceResourceDeploymentNotFound(request.Id, request.DeploymentId)));

        return await runTransaction(
            (session, ct) => DeleteAndApplySnapshotAsync(request, webService, deployment, session, ct),
            cancellationToken);
    }

    private async Task<MongoTransactionDecision<DeleteWebServiceDeploymentHostnameOutcome>> DeleteAndApplySnapshotAsync(
        DeleteWebServiceDeploymentHostnameRequest request,
        WebService webService,
        PlacementSummaryDto deployment,
        MongoTransactionContext<DeleteWebServiceDeploymentHostnameOutcome> transaction,
        CancellationToken cancellationToken)
    {
        var deleted = await deleteCustomHostnameBinding(
            new DeleteCustomHostnameBindingRequest(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                request.Id,
                request.DeploymentId,
                request.BindingId),
            transaction.Session,
            cancellationToken);
        if (deleted is CustomHostnameBindingGitOwned owned)
            return Abort(transaction, new WebServiceDeploymentHostnameError(owned));
        if (deleted is CustomHostnameBindingError deletedError)
            return Abort(transaction, new WebServiceDeploymentHostnameError(deletedError));

        var currentBindings = await loadHostnameBindingsForDeploymentInTransaction(
            new LoadHostnameBindingsForDeploymentInTransactionRequest(
                TenantId: request.TenantId,
                ResourceType: WebServiceResourceDeployment.ResourceType,
                ResourceId: request.Id,
                ResourceDeploymentId: request.DeploymentId),
            session: transaction.Session,
            cancellationToken: cancellationToken);
        var updated = webService.AdvancePlacementDesiredState();
        var applied = await requestResourceDeploymentSetApply(
            new ResourceDeploymentSetApplyRequest(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                request.Id,
                updated.ActiveRevision,
                [
                    new ResourceDeploymentSetApplyItem(
                        deployment.DeploymentId,
                        deployment.DeploymentRevision,
                        deployment.Compute.Profile,
                        new ReplaceIngress(
                            WebServiceDeploymentWorkflow.WithHostnames(
                                deployment,
                                currentBindings.Select(binding => binding.Hostname)))),
                ],
                updated.Lifecycle is AwaitingFirstArtifactWebServiceLifecycle
                    ? ResourceDeploymentActivation.KeepPrepared
                    : ResourceDeploymentActivation.Reconcile),
            transaction.Session,
            cancellationToken);
        if (applied is ResourceDeploymentMutationError failure)
            return Abort(transaction, new WebServiceDeploymentHostnameError(failure));
        var changed = ((MutatedResourceDeployments)applied.Value).Placements.Single(item =>
            item.DeploymentId == request.DeploymentId);

        var advanced = await advanceResourceDeploymentRevision(
            new ResourceDeploymentResourceRevisionRequest(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                request.Id,
                updated.ActiveRevision),
            transaction.Session,
            cancellationToken);
        if (advanced is AdvanceResourceDeploymentRevisionError revisionError)
            return Abort(transaction, new WebServiceDeploymentHostnameError(revisionError));
        var deployments = ((MutatedResourceDeployments)advanced.Value).Placements;

        var persisted = await persistWebService(
            transaction.Session,
            webService,
            updated,
            updated.ToUpdatedEvent(WebServiceDeploymentWorkflow.ToEventDeployments(deployments)),
            cancellationToken);
        if (persisted is WebServiceUpdateConflict persistenceError)
            return Abort(transaction, new WebServiceDeploymentHostnameError(persistenceError));
        if (persisted is WebServiceConnectionConflict connectionConflict)
            return Abort(transaction, new WebServiceDeploymentHostnameError(connectionConflict));
        if (persisted is WebServiceDependencyUnavailable dependencyUnavailable)
            return Abort(transaction, new WebServiceDeploymentHostnameError(dependencyUnavailable));

        if (updated.Lifecycle is ActiveWebServiceLifecycle)
        {
            var release = WebServiceReleaseFactory.CreatePending(
                updated.TenantId,
                updated.Id,
                updated.ActiveRevision,
                WebServiceReleaseAction.HostnameChanged,
                null,
                null,
                updated.Spec,
                WebServiceDeploymentWorkflow.ToReleaseDeployments(deployments),
                null,
                null,
                new WebServiceReleaseFactory.CreateOptions(
                    ObjectId.GenerateNewId().ToString(),
                    timeProvider.GetUtcNow().UtcDateTime),
                updated.Exposure);
            await persistRelease(transaction.Session, release, cancellationToken);
        }

        return transaction.Commit(new DeleteWebServiceDeploymentHostnameOutcome(changed));
    }

    private static DeleteWebServiceDeploymentHostnameOutcome Error(WebServiceDeploymentHostnameError error) =>
        new(error);

    private static MongoTransactionDecision<DeleteWebServiceDeploymentHostnameOutcome> Abort(
        MongoTransactionContext<DeleteWebServiceDeploymentHostnameOutcome> transaction,
        WebServiceDeploymentHostnameError error) => transaction.Abort(Error(error));
}

public sealed class DeleteWebServiceDeploymentHostnameValidator
    : AbstractValidator<DeleteWebServiceDeploymentHostnameRequest>
{
    public DeleteWebServiceDeploymentHostnameValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Web service ID");
        RuleFor(request => request.DeploymentId).MongoObjectId("Resource deployment ID");
        RuleFor(request => request.BindingId).MongoObjectId("Hostname binding ID");
    }
}
