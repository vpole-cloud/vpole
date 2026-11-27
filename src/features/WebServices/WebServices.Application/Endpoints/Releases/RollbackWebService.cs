// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

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
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Queries;
using Pole.Features.WebServices.Application.Releases;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using static Pole.Features.WebServices.Application.Common.WebServiceHttpMappings;

namespace Pole.Features.WebServices.Application.Endpoints.Releases;

public static class RollbackWebService
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapRollbackWebServiceEndpoint()
        {
            app.MapPost(
                    "/{Id}/rollback",
                    async (
                            IMediator mediator, [AsParameters] RollbackWebServiceRequest request,
                            CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
                        {
                            RollbackWebServiceResponse value => Results.Accepted(value: value),
                            RollbackWebServicePublicSpecIncompatible incompatible =>
                                MapRollbackWebServicePublicSpecIncompatible(incompatible),
                            RollbackWebServiceError error => MapError(error),
                        }))
                .WithName("RollbackWebService")
                .Produces<RollbackWebServiceResponse>(StatusCodes.Status202Accepted);
            return app;
        }
    }
}

public sealed record RollbackWebServiceRequest(
    [FromRoute] string Id,
    [FromBody] RollbackWebServiceRequestBody Body,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default
) : IMediatorRequest<RollbackWebServiceOutcome>, ITenantWriteOperation;

public sealed record RollbackWebServiceRequestBody(
    long TargetRevision,
    long? ExpectedRevision = null
);

public sealed record RollbackWebServiceResponse(
    string Id,
    long ResourceRevision,
    string ReleaseId,
    IReadOnlyList<PlacementSummaryDto> Deployments
);

public sealed record RollbackWebServicePublicSpecIncompatible(
    string WebServiceId,
    long TargetRevision
);

public union RollbackWebServiceOutcome(
    RollbackWebServiceResponse,
    RollbackWebServicePublicSpecIncompatible,
    RollbackWebServiceError);

public sealed class RollbackWebServiceHandler(
    LoadWebServiceById loadWebServiceById,
    LoadSucceededWebServiceRelease loadSucceededWebServiceRelease,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    RequestResourceDeploymentSetApply requestResourceDeploymentSetApply,
    PersistUpdatedWebServiceOutcome persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    TimeProvider timeProvider
) : IMediatorOutcomeHandler<RollbackWebServiceRequest, RollbackWebServiceOutcome>
{
    public async Task<RollbackWebServiceOutcome> HandleAsync(
        RollbackWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(request.TenantId, request.Id, cancellationToken);
        if (webService is null)
            return Error(new RollbackWebServiceStateError(new WebServiceNotFound(request.Id)));
        if (request.Body.ExpectedRevision != webService.ActiveRevision)
            return Error(new RollbackWebServiceError(new WebServiceUpdateConflict(webService.Id)));
        if (webService.Lifecycle is not ActiveWebServiceLifecycle || webService.Spec.Image is not ImageInfo)
            return Error(new RollbackWebServiceStateError(new WebServiceUpdateUnavailable(webService.Id)));

        var sourceRelease = await loadSucceededWebServiceRelease(
            request.TenantId,
            request.Id,
            request.Body.TargetRevision,
            cancellationToken);
        if (sourceRelease is null)
            return Error(
                new RollbackWebServiceStateError(
                    new WebServiceReleaseNotFound(webService.Id, request.Body.TargetRevision)));
        if (sourceRelease.Spec.Image is not ImageInfo)
            return Error(new RollbackWebServiceStateError(new WebServiceUpdateUnavailable(webService.Id)));

        if (sourceRelease.ResourceRevision >= webService.ActiveRevision)
            return Error(
                new RollbackWebServiceStateError(
                    new WebServiceRollbackRevisionNotEligible(webService.Id, request.Body.TargetRevision)));
        if (webService.Exposure.Mode is WebServiceExposureMode.Public &&
            !CreateWebServiceRequestBodyValidator.HasPublicIngressTarget(
                sourceRelease.Spec.ToDto().Config.PortMappings))
            return new RollbackWebServiceOutcome(
                new RollbackWebServicePublicSpecIncompatible(webService.Id, sourceRelease.ResourceRevision));

        var deploymentInventory = await loadResourceDeploymentSet(
            new LoadResourceDeploymentSetRequest(
                TenantId: webService.TenantId,
                ResourceType: WebServiceResourceDeployment.ResourceType,
                ResourceId: webService.Id),
            cancellationToken: cancellationToken);
        var deployments = deploymentInventory.Where(WebServiceDeploymentWorkflow.IsDesiredPlacement).ToArray();
        if (deployments.Length == 0)
            return Error(new RollbackWebServiceStateError(new WebServiceHasNoActiveDeployments(webService.Id)));

        var currentQuota = WebServiceDeploymentWorkflow.ToQuotaInput(deployments, webService.Spec);
        var targetQuota = WebServiceDeploymentWorkflow.ToQuotaInput(deployments, sourceRelease.Spec);
        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<WebServiceQuotaInput, RollbackWebServiceOutcome>(
            new ExecuteQuotaAdmissionRequest<WebServiceQuotaInput, RollbackWebServiceOutcome>(
                TenantId: webService.TenantId,
                ResourceKey: WebServiceQuotaResourceKey.Create(webService.Id),
                Mutation: new UpdateQuotaMutation<WebServiceQuotaInput>(currentQuota, targetQuota),
                QuotaError: quotaError => Error(quotaError),
                MutationCallback: async (transaction, ct) =>
                {
                    var updated = webService.Rollback(sourceRelease.Spec);
                    var applied = await requestResourceDeploymentSetApply(
                        new ResourceDeploymentSetApplyRequest(
                            updated.TenantId,
                            WebServiceResourceDeployment.ResourceType,
                            updated.Id,
                            updated.ActiveRevision,
                            deployments.Select(deployment => WebServiceDeploymentWorkflow.ToSetApplyItem(deployment))
                                .ToArray()),
                        transaction.Session,
                        ct);
                    if (applied is ResourceDeploymentMutationError applyFailure)
                        return transaction.Abort(Error(new RollbackWebServiceError(applyFailure)));
                    var updatedDeployments = ((MutatedResourceDeployments)applied.Value).Placements;
                    var updatedEvent = updated.ToUpdatedEvent(
                        WebServiceDeploymentWorkflow.ToEventDeployments(updatedDeployments));

                    var persisted = await persistUpdatedWebService(
                        transaction.Session,
                        webService,
                        updated,
                        updatedEvent,
                        ct);
                    if (persisted is WebServiceUpdateConflict persistenceError)
                        return transaction.Abort(Error(new RollbackWebServiceError(persistenceError)));
                    if (persisted is WebServiceConnectionConflict connectionConflict)
                        return transaction.Abort(Error(new RollbackWebServiceError(connectionConflict)));
                    if (persisted is WebServiceDependencyUnavailable dependencyUnavailable)
                        return transaction.Abort(Error(new RollbackWebServiceError(dependencyUnavailable)));

                    var release = WebServiceReleaseFactory.CreatePending(
                        updated.TenantId,
                        updated.Id,
                        updated.ActiveRevision,
                        WebServiceReleaseAction.Rollback,
                        request.ActorUserId.Value,
                        request.ActorEmail.Value,
                        updated.Spec,
                        WebServiceDeploymentWorkflow.ToReleaseDeployments(updatedDeployments),
                        traceId: updatedEvent.EventCore.TraceId,
                        correlationId: updatedEvent.EventCore.CorrelationId,
                        new WebServiceReleaseFactory.CreateOptions(
                            ObjectId.GenerateNewId().ToString(),
                            timeProvider.GetUtcNow().UtcDateTime),
                        updated.Exposure);
                    await persistCurrentWebServiceRelease(transaction.Session, release, ct);

                    return transaction.Commit(
                        new RollbackWebServiceOutcome(
                            new RollbackWebServiceResponse(
                                updated.Id,
                                updated.ActiveRevision,
                                release.Id,
                                updatedDeployments)));
                }),
            cancellationToken: cancellationToken);
    }

    private static RollbackWebServiceOutcome Error(RollbackWebServiceError error) => new(error);
}

public sealed class RollbackWebServiceValidator : AbstractValidator<RollbackWebServiceRequest>
{
    public RollbackWebServiceValidator()
    {
        RuleFor(request => request.Id).NotEmpty();
        RuleFor(request => request.Body.TargetRevision).GreaterThan(0);
        RuleFor(request => request.Body.ExpectedRevision).NotNull().GreaterThan(0);
    }
}
