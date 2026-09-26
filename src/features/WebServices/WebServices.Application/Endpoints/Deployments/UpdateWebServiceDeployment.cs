using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Contracts;
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

using static Pole.Features.WebServices.Application.Common.WebServiceHttpMappings;

namespace Pole.Features.WebServices.Application.Endpoints.Deployments;

public static class UpdateWebServiceDeployment
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapUpdateWebServiceDeploymentEndpoint()
        {
            app.MapPut(
                    "/{Id}/deployments/{DeploymentId}",
                    async (
                        IMediator mediator,
                        [AsParameters] UpdateWebServiceDeploymentRequest request,
                        CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken))
                        .ToHttpResult(outcome => outcome switch
                        {
                            PlacementSummaryDto value =>
                                Results.Accepted(value: value),
                            UpdateWebServiceDeploymentError error =>
                                MapError(error)
                        }))
                .WithName("UpdateWebServiceDeployment")
                .Produces<PlacementSummaryDto>(StatusCodes.Status202Accepted);
            return app;
        }
    }
}

public sealed record UpdateWebServiceDeploymentRequest(
    [FromRoute] string Id,
    [FromRoute] string DeploymentId,
    [FromBody] UpdateWebServiceDeploymentBody Body,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<UpdateWebServiceDeploymentOutcome>, ITenantWriteOperation;
public sealed record UpdateWebServiceDeploymentBody(
    int FixedReplicas,
    string? ComputeProfileId = null);

public union UpdateWebServiceDeploymentOutcome(
    PlacementSummaryDto,
    UpdateWebServiceDeploymentError);

public sealed class UpdateWebServiceDeploymentHandler(
    LoadWebServiceById loadWebService,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    ResolveComputeProfiles resolveComputeProfiles,
    RequestExactResourceDeploymentApply requestExactResourceDeploymentApply,
    AdvanceResourceDeploymentRevision advanceResourceDeploymentRevision,
    PersistUpdatedWebServiceOutcome persistWebService,
    PersistCurrentWebServiceRelease persistRelease,
    IQuotaAdmissionExecutor quotaAdmission,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<
        UpdateWebServiceDeploymentRequest,
        UpdateWebServiceDeploymentOutcome>
{
    public async Task<UpdateWebServiceDeploymentOutcome> HandleAsync(
        UpdateWebServiceDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebService(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (webService is null)
            return Error(new WebServiceDeploymentMutationStateError(
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
            return Error(new WebServiceDeploymentMutationStateError(
                new WebServiceResourceDeploymentNotFound(
                    request.Id,
                    request.DeploymentId)));
        }

        var replacementCompute = await ResolveReplacementComputeAsync(
            request,
            current,
            cancellationToken);
        if (!replacementCompute.TryGetValue(out var resolution, out var computeFailure))
            return Error(new UpdateWebServiceDeploymentError(computeFailure));
        var replacement = resolution.Value;

        var desiredDeployments = deployments.Select(deployment =>
            deployment.DeploymentId == current.DeploymentId
                ? deployment with
                {
                    FixedReplicas = request.Body.FixedReplicas,
                    Compute = replacement is null
                        ? deployment.Compute
                        : ToComputeSummary(replacement)
                }
                : deployment);
        var currentQuota = WebServiceDeploymentWorkflow.ToQuotaInput(
            deployments,
            webService.Spec);
        var nextQuota = WebServiceDeploymentWorkflow.ToQuotaInput(
            desiredDeployments,
            webService.Spec);

        return await quotaAdmission.ExecuteOutcomeAsync<
            WebServiceQuotaInput,
            UpdateWebServiceDeploymentOutcome>(
            webService.TenantId,
            WebServiceQuotaResourceKey.Create(webService.Id),
            new UpdateQuotaMutation<WebServiceQuotaInput>(currentQuota, nextQuota),
            quotaError => Error(quotaError),
            async (transaction, token) =>
            {
                var updated = webService.AdvancePlacementDesiredState();
                var result = await requestExactResourceDeploymentApply(
                    WebServiceDeploymentWorkflow.ToExactApplyRequest(
                        webService.TenantId,
                        webService.Id,
                        current,
                        updated.ActiveRevision,
                        replacement,
                        fixedReplicas: request.Body.FixedReplicas),
                    transaction.Session,
                    token);
                if (result is ResourceDeploymentMutationError failure)
                    return transaction.Abort(Error(
                        new UpdateWebServiceDeploymentError(failure)));
                var changed = (PlacementSummaryDto)result.Value;

                var advanced = await advanceResourceDeploymentRevision(
                    new ResourceDeploymentResourceRevisionRequest(
                        webService.TenantId,
                        WebServiceResourceDeployment.ResourceType,
                        webService.Id,
                        updated.ActiveRevision),
                    transaction.Session,
                    token);
                if (advanced is AdvanceResourceDeploymentRevisionError revisionFailure)
                    return transaction.Abort(Error(
                        new UpdateWebServiceDeploymentError(revisionFailure)));
                var snapshots = ((MutatedResourceDeployments)advanced.Value).Placements;

                var saved = await persistWebService(
                    transaction.Session,
                    webService,
                    updated,
                    updated.ToUpdatedEvent(
                        WebServiceDeploymentWorkflow.ToEventDeployments(snapshots)),
                    token);
                if (!saved.TryGetValue(out _, out var persistenceError))
                    return transaction.Abort(Error(
                        new UpdateWebServiceDeploymentError(persistenceError)));

                var release = WebServiceReleaseFactory.CreatePending(
                    updated.TenantId,
                    updated.Id,
                    updated.ActiveRevision,
                    WebServiceReleaseAction.UpdateDeployment,
                    null,
                    null,
                    updated.Spec,
                    WebServiceDeploymentWorkflow.ToReleaseDeployments(snapshots),
                    null,
                    null,
                    new WebServiceReleaseFactory.CreateOptions(
                        ObjectId.GenerateNewId().ToString(),
                        timeProvider.GetUtcNow().UtcDateTime));
                await persistRelease(transaction.Session, release, token);
                return transaction.Commit(new UpdateWebServiceDeploymentOutcome(changed));
            },
            cancellationToken);
    }

    private async Task<Result<
        ReplacementComputeResolution,
        ComputeProfileResolutionError>>
        ResolveReplacementComputeAsync(
            UpdateWebServiceDeploymentRequest request,
            PlacementSummaryDto current,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Body.ComputeProfileId))
            return new Ok<ReplacementComputeResolution>(
                new ReplacementComputeResolution(null));

        var resolved = await resolveComputeProfiles(
            new ComputeProfileBatchResolutionRequest(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                [new ComputeProfileTargetResolutionRequest(
                    current.DeploymentTargetId,
                    request.Body.ComputeProfileId)]),
            cancellationToken);
        return resolved switch
        {
            IReadOnlyList<ResolvedComputeProfile> profiles => new Ok<ReplacementComputeResolution>(
                new ReplacementComputeResolution(
                    profiles.Single().ToResourceDeploymentConfiguration())),
            ComputeProfileResolutionError error => new Err<ComputeProfileResolutionError>(error)
        };
    }

    private sealed record ReplacementComputeResolution(
        ResourceDeploymentComputeConfiguration? Value);

    private static ComputeConfigurationSummaryDto ToComputeSummary(
        ResourceDeploymentComputeConfiguration compute) =>
        new(
            new ComputeProfileReference(
                compute.ComputeProfileId,
                compute.ComputeProfileRevision),
            compute.DisplayName,
            new ComputeResourceValuesDto(
                compute.Requests.CpuMillis,
                compute.Requests.MemoryMi),
            new ComputeResourceValuesDto(
                compute.Limits.CpuMillis,
                compute.Limits.MemoryMi),
            []);

    private static UpdateWebServiceDeploymentOutcome Error(
        UpdateWebServiceDeploymentError error) =>
        new(error);
}

public sealed class UpdateWebServiceDeploymentValidator : AbstractValidator<UpdateWebServiceDeploymentRequest>
{
    public UpdateWebServiceDeploymentValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("Web service ID");
        RuleFor(x => x.DeploymentId).MongoObjectId("Resource deployment ID");
        RuleFor(x => x.Body.FixedReplicas).InclusiveBetween(1, 1000);
        RuleFor(x => x.Body.ComputeProfileId)
            .MongoObjectId("Compute profile ID")
            .When(x => !string.IsNullOrWhiteSpace(x.Body.ComputeProfileId));
    }
}
