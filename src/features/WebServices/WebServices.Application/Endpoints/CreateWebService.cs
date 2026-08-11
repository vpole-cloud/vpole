using System.Collections.Immutable;
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
using Pole.Features.Events.Abstractions;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class CreateWebService
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapCreateWebServiceEndpoint()
        {
            app.MapPost("/", async (IMediator mediator, [AsParameters] CreateWebServiceRequest request, CancellationToken cancellationToken) =>
                {
                    var response = await mediator.Send(request, cancellationToken);
                    return response.ToHttpResult(value => Results.Accepted(value: value));
                })
                .WithName("CreateWebService")
                .Produces<CreateWebServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public sealed class CreateWebServiceHandler(
    IResourceGroupOwnershipLookup resourceGroupLookup,
    IDeploymentTargetLookup deploymentTargetLookup,
    ResolveComputeProfiles resolveComputeProfiles,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    ITenantDomainZoneCapability domainZones,
    IHostnameBindingCapability hostnameBindings,
    CreateResourceDeploymentSet createResourceDeploymentSet,
    PersistCreatedWebService persistCreatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    TimeProvider timeProvider)
    : IMediatorHandler<CreateWebServiceRequest, CreateWebServiceResponse>
{
    public async Task<Result<CreateWebServiceResponse>> HandleAsync(
        CreateWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var ownership = await resourceGroupLookup.GetOwnershipAsync(
            request.TenantId,
            request.Body.ResourceGroupId,
            cancellationToken);
        if (!ownership.TryGetValue(out var resourceGroup, out var ownershipFailure))
            return ownershipFailure;
        if (resourceGroup.Lifecycle is not ResourceGroupOwnershipLifecycle.Active)
            return Failures.Conflict("The resource group is being deleted.");

        var isolationScopeId = request.Body.Placement.IsolationScopeId
            ?? resourceGroup.DefaultIsolationScopeId;
        var targetsResult = await ResolveTargetsAsync(
            request.TenantId,
            isolationScopeId,
            request.Body.Placement.Targets,
            cancellationToken);
        if (!targetsResult.TryGetValue(out var targets, out var targetsFailure))
            return targetsFailure;

        var computeResult = await resolveComputeProfiles(
            new ComputeProfileBatchResolutionRequest(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                targets.Select(target => new ComputeProfileTargetResolutionRequest(
                    target.Target.DeploymentTargetId,
                    target.Request.ComputeProfileId)).ToArray()),
            cancellationToken);
        if (!computeResult.TryGetValue(out var computes, out var computeFailure))
            return computeFailure;

        var computeByTarget = computes.ToDictionary(
            compute => compute.DeploymentTargetId,
            StringComparer.Ordinal);
        if (computeByTarget.Count != targets.Count || targets.Any(target =>
                !computeByTarget.ContainsKey(target.Target.DeploymentTargetId)))
        {
            throw new InvalidOperationException(
                "Compute profile batch resolution did not return one result for every selected deployment target.");
        }

        var options = WebServiceFactory.DefaultOptions with { Tags = request.Body.Tags };
        var spec = request.Body.Spec.ToDomain();
        var quotaInput = WebServiceDeploymentWorkflow.ToQuotaInput(
            targets.Select(target => (
                computeByTarget[target.Target.DeploymentTargetId],
                target.Request.FixedReplicas)),
            spec);

        return await quotaAdmissionExecutor.ExecuteAsync<
            WebServiceQuotaInput,
            CreateWebServiceResponse>(
            request.TenantId,
            WebServiceQuotaResourceKey.Create(options.Id),
            new CreateQuotaMutation<WebServiceQuotaInput>(quotaInput),
            async (session, ct) =>
            {
                var webService = WebServiceFactory.Create(
                    request.Body.Name,
                    request.TenantId,
                    request.Body.ResourceGroupId,
                    isolationScopeId,
                    spec,
                    WebServiceExposure.Create(),
                    options);

                var deploymentRequestsResult = await CreateDeploymentRequestsAsync(
                    webService,
                    targets,
                    computeByTarget,
                    session,
                    ct);
                if (!deploymentRequestsResult.TryGetValue(
                        out var deploymentRequests,
                        out var deploymentRequestsFailure))
                {
                    return deploymentRequestsFailure;
                }

                var deploymentsResult = await createResourceDeploymentSet(
                    new CreateResourceDeploymentSetRequest(deploymentRequests),
                    session,
                    ct);
                if (!deploymentsResult.TryGetValue(out var deployments, out var deploymentsFailure))
                    return deploymentsFailure;

                var eventCore = EventCore.Create(webService.TenantId);
                var createdEvent = webService.ToCreateEvent(
                    WebServiceDeploymentWorkflow.ToEventDeployments(deployments));
                await persistCreatedWebService(session, webService, createdEvent, ct);

                var release = WebServiceReleaseFactory.CreatePending(
                    webService.TenantId,
                    webService.Id,
                    webService.ActiveRevision,
                    WebServiceReleaseAction.Create,
                    request.ActorUserId.Value,
                    request.ActorEmail.Value,
                    webService.Spec,
                    WebServiceDeploymentWorkflow.ToReleaseDeployments(deployments),
                    eventCore.TraceId,
                    eventCore.CorrelationId,
                    new WebServiceReleaseFactory.CreateOptions(
                        ObjectId.GenerateNewId().ToString(),
                        timeProvider.GetUtcNow().UtcDateTime));
                await persistCurrentWebServiceRelease(session, release, ct);

                return new CreateWebServiceResponse(webService.Id, deployments);
            },
            cancellationToken);
    }

    private async Task<Result<IReadOnlyList<ResolvedTargetRequest>>> ResolveTargetsAsync(
        string tenantId,
        string isolationScopeId,
        IReadOnlyList<WebServiceDeploymentTargetRequest> requestedTargets,
        CancellationToken cancellationToken)
    {
        var admission = new DeploymentTargetAdmissionRequest(
            tenantId,
            WebServiceResourceDeployment.ResourceType,
            isolationScopeId,
            ExplicitDeploymentTargetId: null,
            DeploymentTargetRequiredCapabilities.PublicIngress);
        var eligibleResult = await deploymentTargetLookup.ListEligibleForNewWorkloadAsync(
            admission,
            cancellationToken);
        if (!eligibleResult.TryGetValue(out var eligible, out var eligibleFailure))
            return eligibleFailure;

        var byId = eligible.ToDictionary(target => target.Id, StringComparer.Ordinal);
        var resolved = new List<ResolvedTargetRequest>(requestedTargets.Count);
        foreach (var requested in requestedTargets)
        {
            DeploymentTargetLookupDto target;
            if (string.IsNullOrWhiteSpace(requested.DeploymentTargetId))
            {
                if (requestedTargets.Count != 1)
                    return AmbiguousPlacement();
                if (eligible.Count == 0)
                    return Failures.Conflict("No deployment target accepts public web-service workloads.");
                if (eligible.Count > 1)
                    return AmbiguousPlacement();
                target = eligible[0];
            }
            else if (!byId.TryGetValue(requested.DeploymentTargetId.Trim(), out target!))
            {
                return Failures.Conflict(
                    "The selected deployment target is unavailable for this web-service placement.");
            }

            var ingress = target.WorkloadConfiguration.Ingress;
            if (ingress is null || string.IsNullOrWhiteSpace(ingress.PublicBaseDomain))
            {
                return Failures.Conflict(
                    "The selected deployment target has no public ingress base-domain configuration.");
            }

            resolved.Add(new ResolvedTargetRequest(
                requested,
                new ResolvedResourceDeploymentTarget(
                    target.Id,
                    target.WorkloadConfiguration.Revision,
                    ResourceDeploymentIngressConfiguration.Create(
                        ingress.IngressClassName,
                        ingress.ClusterIssuerName,
                        ingress.PublicBaseDomain,
                        []))));
        }

        return resolved;
    }

    private async Task<Result<IReadOnlyList<CreateResourceDeploymentRequest>>> CreateDeploymentRequestsAsync(
        WebService webService,
        IReadOnlyList<ResolvedTargetRequest> targets,
        IReadOnlyDictionary<string, ResolvedComputeProfile> computeByTarget,
        MongoDB.Driver.IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var deployments = targets.Select(target => new PendingDeploymentRequest(
            target,
            ObjectId.GenerateNewId().ToString())).ToArray();
        var zoneRequests = new GetOrCreateGeneratedZoneRequest[deployments.Length];
        for (var index = 0; index < deployments.Length; index++)
        {
            var target = deployments[index].Target;
            var ingress = target.Target.Ingress ?? throw new InvalidOperationException(
                "The public-ingress target admission result must contain an ingress snapshot.");
            zoneRequests[index] = new GetOrCreateGeneratedZoneRequest(
                webService.TenantId,
                target.Target.DeploymentTargetId,
                ingress.PublicBaseDomain!,
                target.Target.WorkloadConfigurationRevision);
        }

        var zonesResult = await domainZones.GetOrCreateGeneratedSetAsync(
            zoneRequests,
            session,
            cancellationToken);
        if (!zonesResult.TryGetValue(out var zones, out var zonesFailure))
            return zonesFailure;

        var bindingRequests = new CreateGeneratedHostnameBindingRequest[deployments.Length];
        for (var index = 0; index < deployments.Length; index++)
        {
            var pending = deployments[index];
            bindingRequests[index] = new CreateGeneratedHostnameBindingRequest(
                webService.TenantId,
                zones[index].Id,
                pending.Target.Target.DeploymentTargetId,
                WebServiceResourceDeployment.ResourceType,
                webService.Id,
                pending.DeploymentId,
                $"{webService.Exposure.GeneratedHostnameLabelPrefix}-{webService.Id}");
        }

        var bindingsResult = await hostnameBindings.CreateGeneratedSetAsync(
            bindingRequests,
            session,
            cancellationToken);
        if (!bindingsResult.TryGetValue(out var bindings, out var bindingsFailure))
            return bindingsFailure;

        var requests = new List<CreateResourceDeploymentRequest>(deployments.Length);
        for (var index = 0; index < deployments.Length; index++)
        {
            var pending = deployments[index];
            var target = pending.Target;
            var ingress = target.Target.Ingress!;
            requests.Add(new CreateResourceDeploymentRequest(
                pending.DeploymentId,
                webService.TenantId,
                WebServiceResourceDeployment.ResourceType,
                webService.Id,
                webService.ResourceGroupId,
                webService.IsolationScopeId,
                target.Target with
                {
                    Ingress = ResourceDeploymentIngressConfiguration.Create(
                        ingress.IngressClassName,
                        ingress.ClusterIssuerName,
                        ingress.PublicBaseDomain,
                        [bindings[index].Hostname])
                },
                computeByTarget[target.Target.DeploymentTargetId]
                    .ToResourceDeploymentConfiguration(),
                Storage: null,
                ResourcePlacementCapability.MultiTarget,
                target.Request.FixedReplicas,
                webService.ActiveRevision));
        }

        return requests;
    }

    private static Result<IReadOnlyList<ResolvedTargetRequest>> AmbiguousPlacement() =>
        Failures.SemanticValidation(new Dictionary<string, string[]>
        {
            ["Placement.Targets"] =
            ["A deployment target must be selected when more than one target accepts public web-service workloads."]
        });

    private sealed record ResolvedTargetRequest(
        WebServiceDeploymentTargetRequest Request,
        ResolvedResourceDeploymentTarget Target);

    private sealed record PendingDeploymentRequest(
        ResolvedTargetRequest Target,
        string DeploymentId);
}

public sealed record CreateWebServiceRequest(
    [FromBody] CreateWebServiceRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string> ActorUserId,
    [FromClaim(Claims.Email)] ClaimValue<string> ActorEmail)
    : IMediatorRequest<CreateWebServiceResponse>, ITenantCreateOperation;

public sealed record CreateWebServiceRequestBody(
    string Name,
    string ResourceGroupId,
    WebServiceSpecDto Spec,
    WebServicePlacementRequest Placement,
    ImmutableHashSet<string> Tags);

public sealed record WebServicePlacementRequest(
    string? IsolationScopeId,
    ImmutableList<WebServiceDeploymentTargetRequest> Targets);

public sealed record WebServiceDeploymentTargetRequest(
    string? DeploymentTargetId,
    int FixedReplicas = 1,
    string? ComputeProfileId = null);

public sealed record CreateWebServiceResponse(
    string WebServiceId,
    IReadOnlyList<PlacementSummaryDto> Deployments);

public sealed class CreateWebServiceRequestValidator : AbstractValidator<CreateWebServiceRequest>
{
    public CreateWebServiceRequestValidator()
    {
        RuleFor(request => request.ActorUserId.Value).NotEmpty();
        RuleFor(request => request.ActorEmail.Value).NotEmpty().EmailAddress();
        RuleFor(request => request.Body).SetValidator(new CreateWebServiceRequestBodyValidator());
    }
}

public sealed class CreateWebServiceRequestBodyValidator : AbstractValidator<CreateWebServiceRequestBody>
{
    public CreateWebServiceRequestBodyValidator()
    {
        RuleFor(body => body.Name).NotEmpty().MaximumLength(30);
        RuleFor(body => body.ResourceGroupId).NotEmpty().MongoObjectId("Resource group ID");
        RuleFor(body => body.Spec).NotNull().SetValidator(new WebServiceSpecDtoValidator());
        RuleFor(body => body.Placement).NotNull().SetValidator(new WebServicePlacementRequestValidator());
    }
}

public sealed class WebServicePlacementRequestValidator : AbstractValidator<WebServicePlacementRequest>
{
    public WebServicePlacementRequestValidator()
    {
        RuleFor(placement => placement.IsolationScopeId)
            .OptionalMongoObjectId("Isolation scope ID");
        RuleFor(placement => placement.Targets)
            .NotNull()
            .Must(targets => targets.Count > 0)
            .WithMessage("At least one deployment target is required.");
        RuleForEach(placement => placement.Targets)
            .SetValidator(new WebServiceDeploymentTargetRequestValidator());
        RuleFor(placement => placement.Targets)
            .Must(HaveDistinctExplicitTargetIds)
            .WithMessage("Deployment target IDs must be unique.");
    }

    private static bool HaveDistinctExplicitTargetIds(
        ImmutableList<WebServiceDeploymentTargetRequest>? targets)
    {
        if (targets is null)
            return true;
        var ids = targets
            .Select(target => target.DeploymentTargetId?.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToArray();
        return ids.Distinct(StringComparer.Ordinal).Count() == ids.Length;
    }
}

public sealed class WebServiceDeploymentTargetRequestValidator
    : AbstractValidator<WebServiceDeploymentTargetRequest>
{
    public WebServiceDeploymentTargetRequestValidator()
    {
        RuleFor(target => target.DeploymentTargetId)
            .OptionalMongoObjectId("Deployment target ID");
        RuleFor(target => target.ComputeProfileId)
            .OptionalMongoObjectId("Compute profile ID");
        RuleFor(target => target.FixedReplicas).InclusiveBetween(1, 1000);
    }
}

public sealed class WebServiceSpecDtoValidator : AbstractValidator<WebServiceSpecDto>
{
    public WebServiceSpecDtoValidator()
    {
        RuleFor(spec => spec.Image).NotNull();
        RuleFor(spec => spec.Probes).NotNull();
        RuleFor(spec => spec.Config).NotNull();
    }
}
