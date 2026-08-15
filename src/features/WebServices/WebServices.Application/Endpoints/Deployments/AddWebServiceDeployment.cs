using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class AddWebServiceDeployment
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapAddWebServiceDeploymentEndpoint()
        {
            app.MapPost(
                    "/{Id}/deployments",
                    async (
                        IMediator mediator,
                        [AsParameters] AddWebServiceDeploymentRequest request,
                        CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken))
                        .ToHttpResult(value => Results.Accepted(value: value)))
                .WithName("AddWebServiceDeployment")
                .Produces<PlacementSummaryDto>(StatusCodes.Status202Accepted);
            return app;
        }
    }
}

public sealed record AddWebServiceDeploymentRequest(
    [FromRoute] string Id,
    [FromBody] WebServiceDeploymentTargetRequest Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default)
    : IMediatorRequest<PlacementSummaryDto>, ITenantWriteOperation;

public sealed class AddWebServiceDeploymentHandler(
    LoadWebServiceById loadWebServiceById,
    IResourceDeploymentSetQueryService deploymentQuery,
    IDeploymentTargetPublicIngressAdmission targetAdmission,
    ResolveComputeProfiles resolveComputeProfiles,
    GetOrCreateGeneratedDomainZone getOrCreateGeneratedDomainZone,
    CreateGeneratedHostnameBinding createGeneratedHostnameBinding,
    CreateResourceDeploymentSet createDeployments,
    IResourceDeploymentSetMutationWriter deploymentWriter,
    PersistUpdatedWebService persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    TimeProvider timeProvider)
    : IMediatorHandler<AddWebServiceDeploymentRequest, PlacementSummaryDto>
{
    public async Task<Result<PlacementSummaryDto>> HandleAsync(
        AddWebServiceDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var stateResult = await LoadCurrentStateAsync(request, cancellationToken);
        if (!stateResult.TryGetValue(out var state, out var stateFailure))
            return stateFailure;

        var placementResult = await ResolvePlacementAsync(
            request,
            state.WebService,
            cancellationToken);
        if (!placementResult.TryGetValue(out var placement, out var placementFailure))
            return placementFailure;

        return await ExecuteQuotaMutationAsync(
            request,
            state,
            placement,
            cancellationToken);
    }

    private async Task<Result<CurrentState>> LoadCurrentStateAsync(
        AddWebServiceDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var webService = await loadWebServiceById(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Failures.Conflict("A deleting web service cannot be updated.");

        var deployments = await deploymentQuery.GetAllAsync(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            request.Id,
            cancellationToken);
        var deploymentTargetId = request.Body.DeploymentTargetId!.Trim();
        if (deployments.Any(deployment => string.Equals(
                deployment.DeploymentTargetId,
                deploymentTargetId,
                StringComparison.Ordinal)))
        {
            return Failures.Conflict(
                "The web service already has a deployment on the selected target.");
        }

        return new CurrentState(webService, deployments);
    }

    private async Task<Result<ResolvedAddition>> ResolvePlacementAsync(
        AddWebServiceDeploymentRequest request,
        WebService webService,
        CancellationToken cancellationToken)
    {
        var admission = await targetAdmission.AdmitForNewWorkloadAsync(
            CreateTargetAdmissionRequest(request, webService),
            cancellationToken);
        var targetResult = MapTargetAdmission(admission);
        if (!targetResult.TryGetValue(out var target, out var targetFailure))
            return targetFailure;

        var computeResult = await resolveComputeProfiles(
            CreateComputeResolutionRequest(request, target.DeploymentTargetId),
            cancellationToken);
        return computeResult.TryGetValue(out var computes, out var computeFailure)
            ? new ResolvedAddition(target, computes.Single())
            : computeFailure;
    }

    private static Result<DeploymentTargetPublicIngressDto> MapTargetAdmission(
        DeploymentTargetPublicIngressAdmissionResult admission) => admission switch
        {
            DeploymentTargetPublicIngressDto ingress => ingress,
            InvalidDeploymentTargetId => Failures.ValidationFailure(
                new Dictionary<string, string[]>
                {
                    ["Body.DeploymentTargetId"] =
                    ["Deployment target ID must be a MongoDB ObjectId."]
                }),
            DeploymentTargetNotFound notFound => Failures.NotFound(
                "Deployment target not found.",
                notFound.DeploymentTargetId),
            DeploymentTargetDisabled =>
                Failures.Conflict("The deployment target is disabled."),
            DeploymentTargetDeletionRequested =>
                Failures.Conflict("The deployment target is pending deletion."),
            DeploymentTargetNewWorkloadsDisabled =>
                Failures.Conflict("The deployment target does not accept new workloads."),
            NoDeploymentTargetAcceptsPublicWorkloads =>
                Failures.Conflict("No deployment target accepts public workloads."),
            DeploymentTargetSelectionRequired => Failures.SemanticValidation(
                new Dictionary<string, string[]>
                {
                    ["Body.DeploymentTargetId"] =
                    ["Deployment target is required when more than one eligible target is available."]
                }),
            DeploymentTargetPublicIngressUnavailable => Failures.SemanticValidation(
                new Dictionary<string, string[]>
                {
                    ["Body.DeploymentTargetId"] =
                    ["The selected deployment target does not provide public ingress with a public base domain."]
                })
        };

    private Task<Result<PlacementSummaryDto>> ExecuteQuotaMutationAsync(
        AddWebServiceDeploymentRequest request,
        CurrentState state,
        ResolvedAddition placement,
        CancellationToken cancellationToken)
    {
        var currentQuota = WebServiceDeploymentWorkflow.ToQuotaInput(
            state.Deployments,
            state.WebService.Spec);
        var nextQuota = WebServiceDeploymentWorkflow.ToQuotaInputWithAdditionalDeployment(
            state.Deployments,
            state.WebService.Spec,
            placement.Compute,
            request.Body.FixedReplicas);

        return quotaAdmissionExecutor.ExecuteAsync<
            WebServiceQuotaInput,
            PlacementSummaryDto>(
            state.WebService.TenantId,
            WebServiceQuotaResourceKey.Create(state.WebService.Id),
            new UpdateQuotaMutation<WebServiceQuotaInput>(currentQuota, nextQuota),
            (session, ct) => PersistAsync(request, state, placement, session, ct),
            cancellationToken);
    }

    private async Task<Result<PlacementSummaryDto>> PersistAsync(
        AddWebServiceDeploymentRequest request,
        CurrentState state,
        ResolvedAddition placement,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = state.WebService;
        var updated = current.AdvancePlacementDesiredState();
        var deploymentId = ObjectId.GenerateNewId().ToString();

        var zoneResult = await getOrCreateGeneratedDomainZone(
            CreateGeneratedZoneRequest(current, placement),
            session,
            cancellationToken);
        if (!zoneResult.TryGetValue(out var zone, out var zoneFailure))
            return zoneFailure;

        var bindingResult = await createGeneratedHostnameBinding(
            CreateGeneratedBindingRequest(current, placement, zone.Id, deploymentId),
            session,
            cancellationToken);
        if (!bindingResult.TryGetValue(out var binding, out var bindingFailure))
            return bindingFailure;

        var createdResult = await createDeployments(
            CreateDeploymentSetRequest(
                request,
                updated,
                placement,
                binding.Hostname,
                deploymentId),
            session,
            cancellationToken);
        if (!createdResult.TryGetValue(out var created, out var createdFailure))
            return createdFailure;

        var advancedResult = await deploymentWriter.AdvanceResourceRevisionAsync(
            CreateResourceRevisionRequest(updated),
            session,
            cancellationToken);
        if (!advancedResult.TryGetValue(out var deployments, out var advancedFailure))
            return advancedFailure;

        var persisted = await persistUpdatedWebService(
            session,
            current,
            updated,
            updated.ToUpdatedEvent(
                WebServiceDeploymentWorkflow.ToEventDeployments(deployments)),
            cancellationToken);
        if (persisted is not ISuccess)
            return (Failure)persisted;

        await persistCurrentWebServiceRelease(
            session,
            CreateRelease(request, updated, deployments),
            cancellationToken);
        return created.Single();
    }

    private static DeploymentTargetAdmissionRequest CreateTargetAdmissionRequest(
        AddWebServiceDeploymentRequest request,
        WebService webService) =>
        new(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.IsolationScopeId,
            request.Body.DeploymentTargetId!.Trim(),
            DeploymentTargetRequiredCapabilities.PublicIngress);

    private static ComputeProfileBatchResolutionRequest CreateComputeResolutionRequest(
        AddWebServiceDeploymentRequest request,
        string deploymentTargetId) =>
        new(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            [new ComputeProfileTargetResolutionRequest(
                deploymentTargetId,
                request.Body.ComputeProfileId)]);

    private static GetOrCreateGeneratedZoneRequest CreateGeneratedZoneRequest(
        WebService webService,
        ResolvedAddition placement) =>
        new(
            webService.TenantId,
            placement.PublicIngress.DeploymentTargetId,
            placement.PublicIngress.PublicBaseDomain,
            placement.PublicIngress.WorkloadConfigurationRevision);

    private static CreateGeneratedHostnameBindingRequest CreateGeneratedBindingRequest(
        WebService webService,
        ResolvedAddition placement,
        string zoneId,
        string deploymentId) =>
        new(
            webService.TenantId,
            zoneId,
            placement.PublicIngress.DeploymentTargetId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            deploymentId,
            $"{webService.Exposure.GeneratedHostnameLabelPrefix}-{webService.Id}");

    private static CreateResourceDeploymentSetRequest CreateDeploymentSetRequest(
        AddWebServiceDeploymentRequest request,
        WebService webService,
        ResolvedAddition placement,
        string hostname,
        string deploymentId) =>
        new(
        [
            new CreateResourceDeploymentRequest(
                deploymentId,
                webService.TenantId,
                WebServiceResourceDeployment.ResourceType,
                webService.Id,
                webService.ResourceGroupId,
                webService.IsolationScopeId,
                new ResolvedResourceDeploymentTarget(
                    placement.PublicIngress.DeploymentTargetId,
                    placement.PublicIngress.WorkloadConfigurationRevision,
                    ResourceDeploymentIngressConfiguration.Create(
                        placement.PublicIngress.IngressClassName,
                        placement.PublicIngress.ClusterIssuerName,
                        placement.PublicIngress.PublicBaseDomain,
                        [hostname])),
                placement.Compute.ToResourceDeploymentConfiguration(),
                Storage: null,
                ResourcePlacementCapability.MultiTarget,
                request.Body.FixedReplicas,
                webService.ActiveRevision)
        ]);

    private static ResourceDeploymentResourceRevisionRequest CreateResourceRevisionRequest(
        WebService webService) =>
        new(
            webService.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            webService.ActiveRevision);

    private WebServiceRelease CreateRelease(
        AddWebServiceDeploymentRequest request,
        WebService webService,
        IReadOnlyList<PlacementSummaryDto> deployments) =>
        WebServiceReleaseFactory.CreatePending(
            webService.TenantId,
            webService.Id,
            webService.ActiveRevision,
            WebServiceReleaseAction.AddDeployment,
            request.ActorUserId.Value,
            request.ActorEmail.Value,
            webService.Spec,
            WebServiceDeploymentWorkflow.ToReleaseDeployments(deployments),
            null,
            null,
            new WebServiceReleaseFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                timeProvider.GetUtcNow().UtcDateTime));

    private sealed record CurrentState(
        WebService WebService,
        IReadOnlyList<PlacementSummaryDto> Deployments);

    private sealed record ResolvedAddition(
        DeploymentTargetPublicIngressDto PublicIngress,
        ResolvedComputeProfile Compute);
}

public sealed class AddWebServiceDeploymentValidator : AbstractValidator<AddWebServiceDeploymentRequest>
{
    public AddWebServiceDeploymentValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Web service ID");
        RuleFor(request => request.Body)
            .SetValidator(new WebServiceDeploymentTargetRequestValidator());
        RuleFor(request => request.Body.DeploymentTargetId)
            .NotEmpty()
            .MongoObjectId("Deployment target ID");
    }
}
