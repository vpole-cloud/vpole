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
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Reads;
using Pole.Features.WebServices.Application.Persistence.Releases;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;
using Pole.Workloads;

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
                        IHttpProblemMapper httpProblems,
                        [AsParameters] AddWebServiceDeploymentRequest request,
                        CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken))
                        .ToHttpResult(outcome => outcome switch
                        {
                            PlacementSummaryDto value =>
                                Results.Accepted(value: value),
                            AddWebServiceDeploymentError error =>
                                httpProblems.Map(error).ToHttpResult()
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
    ComputeProfileResolutionError,
    GeneratedDomainZoneError,
    GeneratedHostnameBindingError,
    CreateResourceDeploymentError,
    AdvanceResourceDeploymentRevisionError,
    WebServiceUpdateConflict,
    QuotaExecutionError);

public union AddWebServiceDeploymentOutcome(
    PlacementSummaryDto,
    AddWebServiceDeploymentError);

public sealed class AddWebServiceDeploymentHandler(
    LoadWebServiceById loadWebServiceById,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    AdmitDeploymentTargetPublicIngressForNewWorkload admitPublicIngress,
    ResolveComputeProfiles resolveComputeProfiles,
    GetOrCreateGeneratedDomainZoneOutcome getOrCreateGeneratedDomainZone,
    CreateGeneratedHostnameBindingOutcome createGeneratedHostnameBinding,
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
        if (!stateResult.TryGetValue(out var state, out var stateError))
            return Error(new AddWebServiceDeploymentError(stateError));

        var placementResult = await ResolvePlacementAsync(
            request,
            state.WebService,
            cancellationToken);
        if (!placementResult.TryGetValue(out var placement, out var placementError))
            return Error(placementError);

        return await ExecuteQuotaMutationAsync(
            request,
            state,
            placement,
            cancellationToken);
    }

    private async Task<Result<CurrentState, AddWebServiceDeploymentStateError>>
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
            return new Err<AddWebServiceDeploymentStateError>(
                new AddWebServiceDeploymentStateError(
                    new WebServiceDeploymentNotFound(request.Id)));
        }
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
        {
            return new Err<AddWebServiceDeploymentStateError>(
                new AddWebServiceDeploymentStateError(
                    new WebServiceDeploymentUpdateUnavailable(request.Id)));
        }

        var deployments = await loadResourceDeploymentSet(
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
            return new Err<AddWebServiceDeploymentStateError>(
                new AddWebServiceDeploymentStateError(
                    new WebServiceDeploymentTargetAlreadyExists(
                        request.Id,
                        deploymentTargetId)));
        }

        return new Ok<CurrentState>(new CurrentState(webService, deployments));
    }

    private async Task<Result<ResolvedAddition, AddWebServiceDeploymentError>>
        ResolvePlacementAsync(
        AddWebServiceDeploymentRequest request,
        WebService webService,
        CancellationToken cancellationToken)
    {
        var admission = await admitPublicIngress(
            CreateTargetAdmissionRequest(request, webService),
            cancellationToken);
        var targetResult = ToTargetResult(admission);
        if (!targetResult.TryGetValue(out var target, out var targetError))
        {
            return new Err<AddWebServiceDeploymentError>(
                new AddWebServiceDeploymentError(targetError));
        }

        var computeResult = await resolveComputeProfiles(
            CreateComputeResolutionRequest(request, target.DeploymentTargetId),
            cancellationToken);
        return computeResult.TryGetValue(out var computes, out var computeError)
            ? new Ok<ResolvedAddition>(
                new ResolvedAddition(target, computes.Single()))
            : new Err<AddWebServiceDeploymentError>(
                new AddWebServiceDeploymentError(computeError));
    }

    private static Result<
        DeploymentTargetPublicIngressDto,
        DeploymentTargetPublicIngressAdmissionError> ToTargetResult(
        DeploymentTargetPublicIngressAdmissionResult admission) => admission switch
        {
            DeploymentTargetPublicIngressDto ingress =>
                new Ok<DeploymentTargetPublicIngressDto>(ingress),
            DeploymentTargetNotFound error =>
                TargetError(new DeploymentTargetPublicIngressAdmissionError(error)),
            DeploymentTargetDisabled error =>
                TargetError(new DeploymentTargetPublicIngressAdmissionError(error)),
            DeploymentTargetDeletionRequested error =>
                TargetError(new DeploymentTargetPublicIngressAdmissionError(error)),
            DeploymentTargetNewWorkloadsDisabled error =>
                TargetError(new DeploymentTargetPublicIngressAdmissionError(error)),
            NoDeploymentTargetAcceptsPublicWorkloads error =>
                TargetError(new DeploymentTargetPublicIngressAdmissionError(error)),
            DeploymentTargetSelectionRequired error =>
                TargetError(new DeploymentTargetPublicIngressAdmissionError(error)),
            DeploymentTargetPublicIngressUnavailable error =>
                TargetError(new DeploymentTargetPublicIngressAdmissionError(error))
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
            state.WebService.TenantId,
            WebServiceQuotaResourceKey.Create(state.WebService.Id),
            new UpdateQuotaMutation<WebServiceQuotaInput>(currentQuota, nextQuota),
            quotaError => Error(
                new AddWebServiceDeploymentError(quotaError)),
            (transaction, ct) => PersistAsync(
                request,
                state,
                placement,
                transaction,
                ct),
            cancellationToken);
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

        var zoneResult = await getOrCreateGeneratedDomainZone(
            CreateGeneratedZoneRequest(current, placement),
            transaction.Session,
            cancellationToken);
        if (!zoneResult.TryGetValue(out var zone, out var zoneError))
            return Abort(
                transaction,
                new AddWebServiceDeploymentError(zoneError));

        var bindingResult = await createGeneratedHostnameBinding(
            CreateGeneratedBindingRequest(current, placement, zone.Id, deploymentId),
            transaction.Session,
            cancellationToken);
        if (!bindingResult.TryGetValue(out var binding, out var bindingError))
            return Abort(
                transaction,
                new AddWebServiceDeploymentError(bindingError));

        var createdResult = await createDeployments(
            CreateDeploymentSetRequest(
                request,
                updated,
                placement,
                binding.Hostname,
                deploymentId),
            transaction.Session,
            cancellationToken);
        if (!createdResult.TryGetValue(out var created, out var createdError))
            return Abort(
                transaction,
                new AddWebServiceDeploymentError(createdError));

        var advancedResult = await advanceResourceDeploymentRevision(
            CreateResourceRevisionRequest(updated),
            transaction.Session,
            cancellationToken);
        if (!advancedResult.TryGetValue(out var deployments, out var advancedError))
            return Abort(
                transaction,
                new AddWebServiceDeploymentError(advancedError));

        var persisted = await persistUpdatedWebService(
            transaction.Session,
            current,
            updated,
            updated.ToUpdatedEvent(
                WebServiceDeploymentWorkflow.ToEventDeployments(deployments)),
            cancellationToken);
        if (!persisted.TryGetValue(out _, out var persistenceError))
            return Abort(
                transaction,
                new AddWebServiceDeploymentError(persistenceError));

        await persistCurrentWebServiceRelease(
            transaction.Session,
            CreateRelease(request, updated, deployments),
            cancellationToken);
        return transaction.Commit(
            new AddWebServiceDeploymentOutcome(created.Single()));
    }

    private static Err<DeploymentTargetPublicIngressAdmissionError>
        TargetError(DeploymentTargetPublicIngressAdmissionError error) =>
        new(error);

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
