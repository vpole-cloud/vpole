using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Database;
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
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Releases;
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
            app.MapPost("/", async (
                    IMediator mediator,
                    IHttpProblemMapper httpProblems,
                    [AsParameters] CreateWebServiceRequest request,
                    CancellationToken cancellationToken) =>
                    (await mediator.Send(request, cancellationToken))
                    .ToHttpResult(outcome => outcome switch
                    {
                        CreateWebServiceResponse value => Results.Accepted(value: value),
                        CreateWebServiceError error => httpProblems.Map(error).ToHttpResult()
                    }))
                .WithName("CreateWebService")
                .Produces<CreateWebServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public sealed record CreateWebServiceTargetUnavailable(string DeploymentTargetId);

public union CreateWebServicePlacementError(
    NoDeploymentTargetAcceptsPublicWorkloads,
    DeploymentTargetSelectionRequired,
    CreateWebServiceTargetUnavailable);

public union CreateWebServiceError(
    ResourceGroupOwnershipError,
    CreateWebServicePlacementError,
    ComputeProfileResolutionError,
    GeneratedDomainZoneError,
    GeneratedHostnameBindingError,
    CreateResourceDeploymentError,
    ResourceRegistrationMutationError,
    QuotaExecutionError);

public union CreateWebServiceOutcome(
    CreateWebServiceResponse,
    CreateWebServiceError);

public sealed class CreateWebServiceHandler(
    GetResourceGroupOwnership getResourceGroupOwnership,
    ListEligiblePublicIngressDeploymentTargetsForNewWorkload listEligibleTargets,
    ResolveComputeProfiles resolveComputeProfiles,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    GetOrCreateGeneratedDomainZonesOutcome getOrCreateGeneratedDomainZones,
    CreateGeneratedHostnameBindingsOutcome createGeneratedHostnameBindings,
    CreateResourceDeploymentSet createResourceDeploymentSet,
    RegisterResource registerResource,
    PersistCreatedWebService persistCreatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<CreateWebServiceRequest, CreateWebServiceOutcome>
{
    public async Task<CreateWebServiceOutcome> HandleAsync(
        CreateWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var ownership = await getResourceGroupOwnership(
            request.TenantId,
            request.Body.ResourceGroupId,
            cancellationToken);
        if (!ownership.TryGetValue(out var resourceGroup, out var ownershipError))
            return Error(new CreateWebServiceError(ownershipError));

        var isolationScopeId = request.Body.Placement.IsolationScopeId
            ?? resourceGroup.DefaultIsolationScopeId;
        var targetsResult = await ResolveTargetsAsync(
            request.TenantId,
            isolationScopeId,
            request.Body.Placement.Targets,
            cancellationToken);
        if (!targetsResult.TryGetValue(out var targets, out var targetsError))
            return Error(new CreateWebServiceError(targetsError));

        var computeResult = await resolveComputeProfiles(
            new ComputeProfileBatchResolutionRequest(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                targets.Select(target => new ComputeProfileTargetResolutionRequest(
                    target.Target.DeploymentTargetId,
                    target.Request.ComputeProfileId)).ToArray()),
            cancellationToken);
        if (!computeResult.TryGetValue(out var computes, out var computeError))
            return Error(new CreateWebServiceError(computeError));

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

        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<
            WebServiceQuotaInput,
            CreateWebServiceOutcome>(
            request.TenantId,
            WebServiceQuotaResourceKey.Create(options.Id),
            new CreateQuotaMutation<WebServiceQuotaInput>(quotaInput),
            quotaError => Error(new CreateWebServiceError(quotaError)),
            (transaction, ct) => PersistAsync(
                request,
                isolationScopeId,
                spec,
                options,
                targets,
                computeByTarget,
                transaction,
                ct),
            cancellationToken);
    }

    private async Task<Result<
        IReadOnlyList<ResolvedTargetRequest>,
        CreateWebServicePlacementError>> ResolveTargetsAsync(
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
        var eligible = await listEligibleTargets(
            admission,
            cancellationToken);

        var byId = eligible.ToDictionary(target => target.DeploymentTargetId, StringComparer.Ordinal);
        var resolved = new List<ResolvedTargetRequest>(requestedTargets.Count);
        foreach (var requested in requestedTargets)
        {
            DeploymentTargetPublicIngressDto target;
            if (string.IsNullOrWhiteSpace(requested.DeploymentTargetId))
            {
                if (requestedTargets.Count != 1)
                    return PlacementError(new DeploymentTargetSelectionRequired(
                        eligible.Select(item => item.DeploymentTargetId).ToArray()));
                if (eligible.Count == 0)
                    return PlacementError(new NoDeploymentTargetAcceptsPublicWorkloads());
                if (eligible.Count > 1)
                    return PlacementError(new DeploymentTargetSelectionRequired(
                        eligible.Select(item => item.DeploymentTargetId).ToArray()));
                target = eligible[0];
            }
            else if (!byId.TryGetValue(requested.DeploymentTargetId.Trim(), out target!))
            {
                return PlacementError(new CreateWebServiceTargetUnavailable(
                    requested.DeploymentTargetId.Trim()));
            }

            resolved.Add(new ResolvedTargetRequest(
                requested,
                new ResolvedResourceDeploymentTarget(
                    target.DeploymentTargetId,
                    target.WorkloadConfigurationRevision,
                    ResourceDeploymentIngressConfiguration.Create(
                        target.IngressClassName,
                        target.ClusterIssuerName,
                        target.PublicBaseDomain,
                        []))));
        }

        return new Ok<IReadOnlyList<ResolvedTargetRequest>>(resolved);
    }

    private async Task<Result<
        IReadOnlyList<CreateResourceDeploymentRequest>,
        CreateWebServiceError>> CreateDeploymentRequestsAsync(
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
            var ingress = target.Target.Ingress!;
            zoneRequests[index] = new GetOrCreateGeneratedZoneRequest(
                webService.TenantId,
                target.Target.DeploymentTargetId,
                ingress.PublicBaseDomain!,
                target.Target.WorkloadConfigurationRevision);
        }

        var zonesResult = await getOrCreateGeneratedDomainZones(
            zoneRequests,
            session,
            cancellationToken);
        if (!zonesResult.TryGetValue(out var zones, out var zonesError))
            return new Err<CreateWebServiceError>(new CreateWebServiceError(zonesError));

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

        var bindingsResult = await createGeneratedHostnameBindings(
            bindingRequests,
            session,
            cancellationToken);
        if (!bindingsResult.TryGetValue(out var bindings, out var bindingsError))
            return new Err<CreateWebServiceError>(new CreateWebServiceError(bindingsError));

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

        return new Ok<IReadOnlyList<CreateResourceDeploymentRequest>>(requests);
    }

    private async Task<QuotaAdmissionTransactionDecision<CreateWebServiceOutcome>> PersistAsync(
        CreateWebServiceRequest request,
        string isolationScopeId,
        WebServiceSpec spec,
        WebServiceFactory.CreateOptions options,
        IReadOnlyList<ResolvedTargetRequest> targets,
        IReadOnlyDictionary<string, ResolvedComputeProfile> computeByTarget,
        QuotaAdmissionTransactionContext<CreateWebServiceOutcome> transaction,
        CancellationToken cancellationToken)
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
            transaction.Session,
            cancellationToken);
        if (!deploymentRequestsResult.TryGetValue(out var deploymentRequests, out var requestsError))
            return Abort(transaction, requestsError);

        var deploymentsResult = await createResourceDeploymentSet(
            new CreateResourceDeploymentSetRequest(deploymentRequests),
            transaction.Session,
            cancellationToken);
        if (!deploymentsResult.TryGetValue(out var deployments, out var deploymentsError))
            return Abort(transaction, new CreateWebServiceError(deploymentsError));

        var eventCore = EventCore.Create(webService.TenantId);
        var createdEvent = webService.ToCreateEvent(
            WebServiceDeploymentWorkflow.ToEventDeployments(deployments));
        await persistCreatedWebService(transaction.Session, webService, createdEvent, cancellationToken);
        var registration = await registerResource(
            webService.TenantId,
            webService.ResourceGroupId,
            new ResourceRegistrationDescriptor(
                WebServiceResourceDeployment.ResourceType,
                webService.Id,
                webService.Name),
            transaction.Session,
            cancellationToken);
        if (!registration.TryGetValue(out _, out var registrationError))
            return Abort(transaction, new CreateWebServiceError(registrationError));

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
        await persistCurrentWebServiceRelease(transaction.Session, release, cancellationToken);

        return transaction.Commit(new CreateWebServiceOutcome(
            new CreateWebServiceResponse(webService.Id, deployments)));
    }

    private static CreateWebServiceOutcome Error(CreateWebServiceError error) =>
        new(error);

    private static QuotaAdmissionTransactionDecision<CreateWebServiceOutcome> Abort(
        QuotaAdmissionTransactionContext<CreateWebServiceOutcome> transaction,
        CreateWebServiceError error) =>
        transaction.Abort(Error(error));

    private static Err<CreateWebServicePlacementError> PlacementError(
        NoDeploymentTargetAcceptsPublicWorkloads error) =>
        new(new CreateWebServicePlacementError(error));

    private static Err<CreateWebServicePlacementError> PlacementError(
        DeploymentTargetSelectionRequired error) =>
        new(new CreateWebServicePlacementError(error));

    private static Err<CreateWebServicePlacementError> PlacementError(
        CreateWebServiceTargetUnavailable error) =>
        new(new CreateWebServicePlacementError(error));

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
    : IMediatorRequest<CreateWebServiceOutcome>, ITenantCreateOperation;

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
