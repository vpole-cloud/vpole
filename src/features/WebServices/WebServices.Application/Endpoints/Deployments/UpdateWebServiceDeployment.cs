using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Features.WebServices.Domain;
using MongoDB.Bson;
using Pole.Validation;

namespace Pole.Features.WebServices.Application.Endpoints;

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
                        .ToHttpResult(value => Results.Accepted(value: value)))
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
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<PlacementSummaryDto>, ITenantWriteOperation;
public sealed record UpdateWebServiceDeploymentBody(
    int FixedReplicas,
    string? ComputeProfileId = null);

public sealed class UpdateWebServiceDeploymentHandler(
    LoadWebServiceById loadWebService,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    ResolveComputeProfilesLegacy resolveComputeProfiles,
    RequestExactResourceDeploymentApplyLegacy requestExactResourceDeploymentApply,
    AdvanceResourceDeploymentRevisionLegacy advanceResourceDeploymentRevision,
    PersistUpdatedWebService persistWebService,
    PersistCurrentWebServiceRelease persistRelease,
    IQuotaAdmissionExecutor quotaAdmission,
    TimeProvider timeProvider)
    : IMediatorHandler<UpdateWebServiceDeploymentRequest, PlacementSummaryDto>
{
    public async Task<Result<PlacementSummaryDto>> HandleAsync(
        UpdateWebServiceDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebService(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);

        var deployments = await loadResourceDeploymentSet(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            request.Id,
            cancellationToken);
        var current = deployments.SingleOrDefault(deployment =>
            deployment.DeploymentId == request.DeploymentId);
        if (current is null)
        {
            return Failures.NotFound(
                "Resource deployment not found.",
                request.DeploymentId);
        }

        var replacementCompute = await ResolveReplacementComputeAsync(
            request,
            current,
            cancellationToken);
        if (!replacementCompute.TryGetValue(out var resolution, out var computeFailure))
            return computeFailure;
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

        return await quotaAdmission.ExecuteAsync<WebServiceQuotaInput, PlacementSummaryDto>(
            webService.TenantId,
            WebServiceQuotaResourceKey.Create(webService.Id),
            new UpdateQuotaMutation<WebServiceQuotaInput>(currentQuota, nextQuota),
            async (session, token) =>
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
                    session,
                    token);
                if (!result.TryGetValue(out var changed, out var failure))
                    return failure;

                var advanced = await advanceResourceDeploymentRevision(
                    new ResourceDeploymentResourceRevisionRequest(
                        webService.TenantId,
                        WebServiceResourceDeployment.ResourceType,
                        webService.Id,
                        updated.ActiveRevision),
                    session,
                    token);
                if (!advanced.TryGetValue(out var snapshots, out failure))
                    return failure;

                var saved = await persistWebService(
                    session,
                    webService,
                    updated,
                    updated.ToUpdatedEvent(
                        WebServiceDeploymentWorkflow.ToEventDeployments(snapshots)),
                    token);
                if (saved is not ISuccess)
                    return (Failure)saved;

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
                await persistRelease(session, release, token);
                return changed;
            },
            cancellationToken);
    }

    private async Task<Result<ReplacementComputeResolution>>
        ResolveReplacementComputeAsync(
            UpdateWebServiceDeploymentRequest request,
            PlacementSummaryDto current,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Body.ComputeProfileId))
            return new ReplacementComputeResolution(null);

        var resolved = await resolveComputeProfiles(
            new ComputeProfileBatchResolutionRequest(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                [new ComputeProfileTargetResolutionRequest(
                    current.DeploymentTargetId,
                    request.Body.ComputeProfileId)]),
            cancellationToken);
        return resolved.TryGetValue(out var profiles, out var failure)
            ? new ReplacementComputeResolution(
                profiles.Single().ToResourceDeploymentConfiguration())
            : failure;
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
