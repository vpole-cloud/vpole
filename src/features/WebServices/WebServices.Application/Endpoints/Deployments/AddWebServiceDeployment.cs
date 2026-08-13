using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
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
    IDeploymentTargetLookup targetLookup,
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
        if (string.IsNullOrWhiteSpace(request.Body.DeploymentTargetId))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["DeploymentTargetId"] =
                ["Deployment target ID is required when adding a deployment."]
            });
        }

        var webService = await loadWebServiceById(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Failures.Conflict("A deleting web service cannot be updated.");

        var current = await deploymentQuery.GetAllAsync(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            request.Id,
            cancellationToken);
        if (current.Any(deployment => string.Equals(
                deployment.DeploymentTargetId,
                request.Body.DeploymentTargetId.Trim(),
                StringComparison.Ordinal)))
        {
            return Failures.Conflict("The web service already has a deployment on the selected target.");
        }

        var admission = new DeploymentTargetAdmissionRequest(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.IsolationScopeId,
            request.Body.DeploymentTargetId,
            DeploymentTargetRequiredCapabilities.PublicIngress);
        var targetResult = await targetLookup.ResolveForNewWorkloadAsync(
            admission,
            cancellationToken);
        if (!targetResult.TryGetValue(out var target, out var targetFailure))
            return targetFailure;
        var ingress = target.WorkloadConfiguration.Ingress;
        if (ingress is null || string.IsNullOrWhiteSpace(ingress.PublicBaseDomain))
        {
            return Failures.Conflict(
                "The selected deployment target has no public ingress base-domain configuration.");
        }

        var computeResult = await resolveComputeProfiles(
            new ComputeProfileBatchResolutionRequest(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                [new ComputeProfileTargetResolutionRequest(
                    target.Id,
                    request.Body.ComputeProfileId)]),
            cancellationToken);
        if (!computeResult.TryGetValue(out var computes, out var computeFailure))
            return computeFailure;
        var compute = computes.Single();

        var currentQuota = WebServiceDeploymentWorkflow.ToQuotaInput(current, webService.Spec);
        var nextQuota = WebServiceDeploymentWorkflow.ToQuotaInput(
            current.Append(CreateQuotaProjection(
                webService,
                target.Id,
                compute,
                request.Body.FixedReplicas)),
            webService.Spec);

        return await quotaAdmissionExecutor.ExecuteAsync<WebServiceQuotaInput, PlacementSummaryDto>(
            webService.TenantId,
            WebServiceQuotaResourceKey.Create(webService.Id),
            new UpdateQuotaMutation<WebServiceQuotaInput>(currentQuota, nextQuota),
            async (session, ct) =>
            {
                var updatedWebService = webService.AdvancePlacementDesiredState();
                var deploymentId = ObjectId.GenerateNewId().ToString();
                var zoneResult = await getOrCreateGeneratedDomainZone(
                    new GetOrCreateGeneratedZoneRequest(
                        webService.TenantId,
                        target.Id,
                        ingress.PublicBaseDomain!,
                        target.WorkloadConfiguration.Revision),
                    session,
                    ct);
                if (!zoneResult.TryGetValue(out var zone, out var zoneFailure))
                    return zoneFailure;
                var bindingResult = await createGeneratedHostnameBinding(
                    new CreateGeneratedHostnameBindingRequest(
                        webService.TenantId,
                        zone.Id,
                        target.Id,
                        WebServiceResourceDeployment.ResourceType,
                        webService.Id,
                        deploymentId,
                        $"{webService.Exposure.GeneratedHostnameLabelPrefix}-{webService.Id}"),
                    session,
                    ct);
                if (!bindingResult.TryGetValue(out var binding, out var bindingFailure))
                    return bindingFailure;

                var created = await createDeployments(
                    new CreateResourceDeploymentSetRequest(
                    [
                        new CreateResourceDeploymentRequest(
                            deploymentId,
                            webService.TenantId,
                            WebServiceResourceDeployment.ResourceType,
                            webService.Id,
                            webService.ResourceGroupId,
                            webService.IsolationScopeId,
                            new ResolvedResourceDeploymentTarget(
                                target.Id,
                                target.WorkloadConfiguration.Revision,
                                ResourceDeploymentIngressConfiguration.Create(
                                    ingress.IngressClassName,
                                    ingress.ClusterIssuerName,
                                    ingress.PublicBaseDomain,
                                    [binding.Hostname])),
                            compute.ToResourceDeploymentConfiguration(),
                            Storage: null,
                            ResourcePlacementCapability.MultiTarget,
                            request.Body.FixedReplicas,
                            updatedWebService.ActiveRevision)
                    ]),
                    session,
                    ct);
                if (!created.TryGetValue(out var createdDeployments, out var createdFailure))
                    return createdFailure;

                var advanced = await deploymentWriter.AdvanceResourceRevisionAsync(
                    new ResourceDeploymentResourceRevisionRequest(
                        webService.TenantId,
                        WebServiceResourceDeployment.ResourceType,
                        webService.Id,
                        updatedWebService.ActiveRevision),
                    session,
                    ct);
                if (!advanced.TryGetValue(out var allDeployments, out var advancedFailure))
                    return advancedFailure;
                var persisted = await persistUpdatedWebService(
                    session,
                    webService,
                    updatedWebService,
                    updatedWebService.ToUpdatedEvent(
                        WebServiceDeploymentWorkflow.ToEventDeployments(allDeployments)),
                    ct);
                if (persisted is not ISuccess)
                    return (Failure)persisted;

                var release = WebServiceReleaseFactory.CreatePending(
                    webService.TenantId,
                    webService.Id,
                    updatedWebService.ActiveRevision,
                    WebServiceReleaseAction.AddDeployment,
                    request.ActorUserId.Value,
                    request.ActorEmail.Value,
                    updatedWebService.Spec,
                    WebServiceDeploymentWorkflow.ToReleaseDeployments(allDeployments),
                    null,
                    null,
                    new WebServiceReleaseFactory.CreateOptions(
                        ObjectId.GenerateNewId().ToString(),
                        timeProvider.GetUtcNow().UtcDateTime));
                await persistCurrentWebServiceRelease(session, release, ct);
                return createdDeployments.Single();
            }, cancellationToken);
    }

    private static PlacementSummaryDto CreateQuotaProjection(
        WebService webService,
        string deploymentTargetId,
        ResolvedComputeProfile compute,
        int fixedReplicas) =>
        new(
            ObjectId.GenerateNewId().ToString(),
            webService.IsolationScopeId,
            string.Empty,
            deploymentTargetId,
            DesiredGeneration: 1,
            ObservedGeneration: 0,
            Status: "Pending",
            FailureCode: null,
            new ComputeConfigurationSummaryDto(
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
                []),
            [],
            FixedReplicas: fixedReplicas);
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
