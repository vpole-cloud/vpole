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
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.SourceDelivery;
using Pole.Features.WebServices.Application.Connections;
using Pole.Features.WebServices.Application.Queries;
using Pole.Features.WebServices.Application.Releases;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;
using Pole.Workloads;

using static Pole.Features.WebServices.Application.Common.WebServiceHttpMappings;

namespace Pole.Features.WebServices.Application.Endpoints.Deployments;

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
                        .ToHttpResult(outcome => outcome switch
                        {
                            PlacementSummaryDto value =>
                                Results.Accepted(value: value),
                            AddWebServiceDeploymentError error =>
                                MapError(error)
                        }))
                .WithName("AddWebServiceDeployment")
                .Produces<PlacementSummaryDto>(StatusCodes.Status202Accepted)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
            return app;
        }
    }
}

public sealed record AddWebServiceDeploymentRequest(
    [FromRoute] string Id,
    [FromBody] WebServiceDeploymentTargetRequest Body,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default)
    : IMediatorRequest<AddWebServiceDeploymentOutcome>, ITenantWriteOperation;

public sealed record WebServiceDeploymentNotFound(string WebServiceId);

public sealed record WebServiceDeploymentUpdateUnavailable(string WebServiceId);

public sealed record WebServiceDeploymentTargetAlreadyExists(
    string WebServiceId,
    string DeploymentTargetId);

public union AddWebServiceDeploymentStateError(
    WebServiceDeploymentNotFound,
    WebServiceDeploymentUpdateUnavailable,
    WebServiceDeploymentTargetAlreadyExists);

public union AddWebServiceDeploymentError(
    AddWebServiceDeploymentStateError,
    DeploymentTargetPublicIngressAdmissionError,
    DeploymentTargetNewWorkloadAdmissionError,
    ComputeProfileResolutionError,
    GeneratedDomainZoneError,
    GeneratedHostnameBindingError,
    CreateResourceDeploymentError,
    AdvanceResourceDeploymentRevisionError,
    WebServiceConnectionConflict,
    WebServiceDependencyUnavailable,
    WebServiceUpdateConflict,
    QuotaExecutionError);

public union AddWebServiceDeploymentOutcome(
    PlacementSummaryDto,
    AddWebServiceDeploymentError);

public sealed class AddWebServiceDeploymentHandler(
    LoadWebServiceById loadWebServiceById,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    AdmitDeploymentTargetForNewWorkload admitDeploymentTarget,
    AdmitDeploymentTargetPublicIngressForNewWorkload admitPublicIngress,
    ResolveComputeProfiles resolveComputeProfiles,
    GetOrCreateGeneratedDomainZone getOrCreateGeneratedDomainZone,
    CreateGeneratedHostnameBinding createGeneratedHostnameBinding,
    CreateResourceDeploymentSet createDeployments,
    AdvanceResourceDeploymentRevision advanceResourceDeploymentRevision,
    PersistUpdatedWebServiceOutcome persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<
        AddWebServiceDeploymentRequest,
        AddWebServiceDeploymentOutcome>
{
    public async Task<AddWebServiceDeploymentOutcome> HandleAsync(
        AddWebServiceDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var stateResult = await LoadCurrentStateAsync(request, cancellationToken);
        if (stateResult is AddWebServiceDeploymentStateError stateError)
            return Error(new AddWebServiceDeploymentError(stateError));

        var state = (CurrentState)stateResult.Value;
        var placementResult = await ResolvePlacementAsync(
            request,
            state.WebService,
            cancellationToken);
        if (placementResult is AddWebServiceDeploymentError placementError)
            return Error(placementError);

        var placement = (ResolvedAddition)placementResult.Value;
        return await ExecuteQuotaMutationAsync(
            request,
            state,
            placement,
            cancellationToken);
    }

    private async Task<LoadCurrentStateOutcome>
        LoadCurrentStateAsync(
        AddWebServiceDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var webService = await loadWebServiceById(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (webService is null)
        {
            return new AddWebServiceDeploymentStateError(
                new WebServiceDeploymentNotFound(request.Id));
        }
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
        {
            return new AddWebServiceDeploymentStateError(
                new WebServiceDeploymentUpdateUnavailable(request.Id));
        }

        var deployments = await loadResourceDeploymentSet(
            new LoadResourceDeploymentSetRequest(
                TenantId: request.TenantId,
                ResourceType: WebServiceResourceDeployment.ResourceType,
                ResourceId: request.Id),
            cancellationToken: cancellationToken);
        var deploymentTargetId = request.Body.DeploymentTargetId!.Trim();
        if (deployments.Any(deployment => string.Equals(
                deployment.DeploymentTargetId,
                deploymentTargetId,
                StringComparison.Ordinal)))
        {
            return new AddWebServiceDeploymentStateError(
                new WebServiceDeploymentTargetAlreadyExists(
                    request.Id,
                    deploymentTargetId));
        }

        return new CurrentState(webService, deployments);
    }

    private async Task<ResolvePlacementOutcome>
        ResolvePlacementAsync(
        AddWebServiceDeploymentRequest request,
        WebService webService,
        CancellationToken cancellationToken)
    {
        ResolvedAddition placement;
        if (webService.Exposure.Mode is WebServiceExposureMode.Public)
        {
            var admission = await admitPublicIngress(
                CreateTargetAdmissionRequest(request, webService),
                cancellationToken);
            var targetResult = ToTargetResult(admission);
            if (targetResult is DeploymentTargetPublicIngressAdmissionError targetError)
                return new AddWebServiceDeploymentError(targetError);
            var target = (DeploymentTargetPublicIngressDto)targetResult.Value;
            var computeResult = await resolveComputeProfiles(
                CreateComputeResolutionRequest(request, target.DeploymentTargetId),
                cancellationToken);
            if (computeResult is ComputeProfileResolutionError publicComputeError)
                return new AddWebServiceDeploymentError(publicComputeError);
            placement = new ResolvedAddition(
                target.DeploymentTargetId,
                target.WorkloadConfigurationRevision,
                ResourceDeploymentIngressConfiguration.Create(
                    target.IngressClassName,
                    target.ClusterIssuerName,
                    target.PublicBaseDomain,
                    []),
                ((IReadOnlyList<ResolvedComputeProfile>)computeResult.Value).Single());
        }
        else
        {
            var admission = await admitDeploymentTarget(
                new DeploymentTargetAdmissionRequest(
                    request.TenantId,
                    WebServiceResourceDeployment.ResourceType,
                    webService.IsolationScopeId,
                    request.Body.DeploymentTargetId!.Trim(),
                    DeploymentTargetRequiredCapabilities.None),
                cancellationToken);
            if (admission is DeploymentTargetNewWorkloadAdmissionError targetError)
                return new AddWebServiceDeploymentError(targetError);
            var target = (DeploymentTargetLookupDto)admission.Value;
            var computeResult = await resolveComputeProfiles(
                CreateComputeResolutionRequest(request, target.Id),
                cancellationToken);
            if (computeResult is ComputeProfileResolutionError computeError)
                return new AddWebServiceDeploymentError(computeError);
            placement = new ResolvedAddition(
                target.Id,
                target.WorkloadConfiguration.Revision,
                null,
                ((IReadOnlyList<ResolvedComputeProfile>)computeResult.Value).Single());
        }
        return placement;
    }

    private static TargetResolutionOutcome ToTargetResult(
        DeploymentTargetPublicIngressAdmissionResult admission) => admission switch
        {
            DeploymentTargetPublicIngressDto ingress => ingress,
            DeploymentTargetNotFound error =>
                new DeploymentTargetPublicIngressAdmissionError(error),
            DeploymentTargetDisabled error =>
                new DeploymentTargetPublicIngressAdmissionError(error),
            DeploymentTargetDeletionRequested error =>
                new DeploymentTargetPublicIngressAdmissionError(error),
            DeploymentTargetNewWorkloadsDisabled error =>
                new DeploymentTargetPublicIngressAdmissionError(error),
            NoDeploymentTargetAcceptsPublicWorkloads error =>
                new DeploymentTargetPublicIngressAdmissionError(error),
            DeploymentTargetSelectionRequired error =>
                new DeploymentTargetPublicIngressAdmissionError(error),
            DeploymentTargetPublicIngressUnavailable error =>
                new DeploymentTargetPublicIngressAdmissionError(error)
        };

    private Task<AddWebServiceDeploymentOutcome> ExecuteQuotaMutationAsync(
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

        return quotaAdmissionExecutor.ExecuteOutcomeAsync<
            WebServiceQuotaInput,
            AddWebServiceDeploymentOutcome>(
            new ExecuteQuotaAdmissionRequest<global::Pole.Features.WebServices.Application.Common.WebServiceQuotaInput,
                global::Pole.Features.WebServices.Application.Endpoints.Deployments.AddWebServiceDeploymentOutcome>(
                TenantId: state.WebService.TenantId,
                ResourceKey: WebServiceQuotaResourceKey.Create(state.WebService.Id),
                Mutation: new UpdateQuotaMutation<WebServiceQuotaInput>(currentQuota, nextQuota),
                QuotaError: quotaError => Error(
                new AddWebServiceDeploymentError(quotaError)),
                MutationCallback: (transaction, ct) => PersistAsync(
                request,
                state,
                placement,
                transaction,
                ct)),
            cancellationToken: cancellationToken);
    }

    private async Task<QuotaAdmissionTransactionDecision<AddWebServiceDeploymentOutcome>>
        PersistAsync(
        AddWebServiceDeploymentRequest request,
        CurrentState state,
        ResolvedAddition placement,
        QuotaAdmissionTransactionContext<AddWebServiceDeploymentOutcome> transaction,
        CancellationToken cancellationToken)
    {
        var current = state.WebService;
        var updated = current.AdvancePlacementDesiredState();
        var deploymentId = ObjectId.GenerateNewId().ToString();
        string? hostname = null;
        if (placement.Ingress is not null)
        {
            var zoneResult = await getOrCreateGeneratedDomainZone(
                CreateGeneratedZoneRequest(current, placement),
                transaction.Session,
                cancellationToken);
            if (zoneResult is GeneratedDomainZoneError zoneError)
                return Abort(transaction, new AddWebServiceDeploymentError(zoneError));

            var zone = (TenantDomainZoneSnapshot)zoneResult.Value;
            var bindingResult = await createGeneratedHostnameBinding(
                CreateGeneratedBindingRequest(current, placement, zone.Id, deploymentId),
                transaction.Session,
                cancellationToken);
            if (bindingResult is GeneratedHostnameBindingError bindingError)
                return Abort(transaction, new AddWebServiceDeploymentError(bindingError));
            hostname = ((HostnameBindingSnapshot)bindingResult.Value).Hostname;
        }

        var createdResult = await createDeployments(
            CreateDeploymentSetRequest(
                request,
                updated,
                placement,
                hostname,
                deploymentId),
            transaction.Session,
            cancellationToken);
        if (createdResult is CreateResourceDeploymentError createdError)
            return Abort(
                transaction,
                new AddWebServiceDeploymentError(createdError));
        var created = ((CreatedResourceDeployments)createdResult.Value).Placements;

        var advancedResult = await advanceResourceDeploymentRevision(
            CreateResourceRevisionRequest(updated),
            transaction.Session,
            cancellationToken);
        if (advancedResult is AdvanceResourceDeploymentRevisionError advancedError)
            return Abort(
                transaction,
                new AddWebServiceDeploymentError(advancedError));
        var deployments = ((MutatedResourceDeployments)advancedResult.Value).Placements;

        var persisted = await persistUpdatedWebService(
            transaction.Session,
            current,
            updated,
            updated.ToUpdatedEvent(
                WebServiceDeploymentWorkflow.ToEventDeployments(deployments)),
            cancellationToken);
        if (persisted is WebServiceUpdateConflict persistenceError)
            return Abort(
                transaction,
                new AddWebServiceDeploymentError(persistenceError));
        if (persisted is WebServiceConnectionConflict connectionConflict)
            return Abort(
                transaction,
                new AddWebServiceDeploymentError(connectionConflict));
        if (persisted is WebServiceDependencyUnavailable dependencyUnavailable)
            return Abort(
                transaction,
                new AddWebServiceDeploymentError(dependencyUnavailable));

        await persistCurrentWebServiceRelease(
            transaction.Session,
            CreateRelease(request, updated, deployments),
            cancellationToken);
        return transaction.Commit(
            new AddWebServiceDeploymentOutcome(created.Single()));
    }

    private static AddWebServiceDeploymentOutcome Error(
        AddWebServiceDeploymentError error) =>
        new(error);

    private static QuotaAdmissionTransactionDecision<AddWebServiceDeploymentOutcome>
        Abort(
            QuotaAdmissionTransactionContext<AddWebServiceDeploymentOutcome> transaction,
            AddWebServiceDeploymentError error) =>
        transaction.Abort(Error(error));

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
            placement.DeploymentTargetId,
            placement.Ingress!.PublicBaseDomain!,
            placement.TargetWorkloadConfigurationRevision);

    private static CreateGeneratedHostnameBindingRequest CreateGeneratedBindingRequest(
        WebService webService,
        ResolvedAddition placement,
        string zoneId,
        string deploymentId) =>
        new(
            webService.TenantId,
            zoneId,
            placement.DeploymentTargetId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            deploymentId,
            webService.Exposure.GeneratedHostnameLabel(webService.Id));

    private static CreateResourceDeploymentSetRequest CreateDeploymentSetRequest(
        AddWebServiceDeploymentRequest request,
        WebService webService,
        ResolvedAddition placement,
        string? hostname,
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
                    placement.DeploymentTargetId,
                    placement.TargetWorkloadConfigurationRevision,
                    placement.Ingress is null
                        ? null
                        : ResourceDeploymentIngressConfiguration.Create(
                            placement.Ingress.IngressClassName,
                            placement.Ingress.ClusterIssuerName,
                            placement.Ingress.PublicBaseDomain!,
                            [hostname ?? throw new InvalidOperationException(
                                "A public deployment hostname is required.")])),
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
                timeProvider.GetUtcNow().UtcDateTime),
            webService.Exposure);

    private sealed record CurrentState(
        WebService WebService,
        IReadOnlyList<PlacementSummaryDto> Deployments);

    private sealed record ResolvedAddition(
        string DeploymentTargetId,
        long TargetWorkloadConfigurationRevision,
        ResourceDeploymentIngressConfiguration? Ingress,
        ResolvedComputeProfile Compute);

    private union LoadCurrentStateOutcome(
        CurrentState,
        AddWebServiceDeploymentStateError);

    private union ResolvePlacementOutcome(
        ResolvedAddition,
        AddWebServiceDeploymentError);

    private union TargetResolutionOutcome(
        DeploymentTargetPublicIngressDto,
        DeploymentTargetPublicIngressAdmissionError);
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
