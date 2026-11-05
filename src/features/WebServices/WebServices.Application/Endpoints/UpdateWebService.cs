using System.Collections.Immutable;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.SourceDelivery;
using Pole.Features.WebServices.Application.Connections;
using Pole.Features.WebServices.Application.Queries;
using Pole.Features.WebServices.Application.Releases;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;

using static Pole.Features.WebServices.Application.Common.WebServiceHttpMappings;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class UpdateWebService
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapUpdateWebServiceEndpoint()
        {
            app.MapPut("/{Id}", async (
                    IMediator mediator,
                    [AsParameters] UpdateWebServiceRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        UpdateWebServiceResponse value =>
                            Results.Accepted(value: value),
                        UpdateWebServiceError error =>
                            MapError(error)
                    });
                })
                .WithName("UpdateWebService")
                .Produces<UpdateWebServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public sealed record UpdateWebServiceRequest(
    [FromRoute] string Id,
    [FromBody] UpdateWebServiceRequestBody Body,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default)
    : IMediatorRequest<UpdateWebServiceOutcome>, ITenantWriteOperation;

public sealed record UpdateWebServiceRequestBody(
    WebServiceSpecDto Spec,
    ImmutableHashSet<string> Tags,
    [property: JsonConverter(typeof(WebServiceExposureModeJsonConverter))]
    WebServiceExposureMode Exposure,
    long? ExpectedRevision = null);

public sealed record UpdateWebServiceResponse(
    string Id,
    long ResourceRevision,
    IReadOnlyList<PlacementSummaryDto> Deployments);

public union UpdateWebServiceOutcome(
    UpdateWebServiceResponse,
    UpdateWebServiceError);

public sealed class UpdateWebServiceHandler(
    LoadWebServiceById loadWebServiceById,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    AdmitDeploymentTargetPublicIngressForNewWorkload admitPublicIngress,
    GetOrCreateGeneratedDomainZones getOrCreateGeneratedDomainZones,
    CreateGeneratedHostnameBindings createGeneratedHostnameBindings,
    LoadHostnameBindingsForDeploymentInTransaction loadHostnameBindingsForDeployment,
    RequestResourceDeploymentSetApply requestResourceDeploymentSetApply,
    AdvanceResourceDeploymentRevision advanceResourceDeploymentRevision,
    PersistUpdatedWebServiceOutcome persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<UpdateWebServiceRequest, UpdateWebServiceOutcome>
{
    public async Task<UpdateWebServiceOutcome> HandleAsync(
        UpdateWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(request.TenantId, request.Id, cancellationToken);
        if (webService is null)
            return Error(new WebServiceLifecycleMutationStateError(
                new WebServiceNotFound(request.Id)));
        if (request.Body.ExpectedRevision is not null && request.Body.ExpectedRevision != webService.ActiveRevision)
            return Error(new UpdateWebServiceError(new WebServiceUpdateConflict(webService.Id)));
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Error(new WebServiceLifecycleMutationStateError(
                new WebServiceUpdateUnavailable(webService.Id)));

        var deploymentInventory = await loadResourceDeploymentSet(
            new LoadResourceDeploymentSetRequest(
                TenantId: webService.TenantId,
                ResourceType: WebServiceResourceDeployment.ResourceType,
                ResourceId: webService.Id),
            cancellationToken: cancellationToken);
        var deployments = deploymentInventory
            .Where(WebServiceDeploymentWorkflow.IsDesiredPlacement)
            .ToArray();
        if (deployments.Length == 0)
            return Error(new WebServiceLifecycleMutationStateError(
                new WebServiceHasNoActiveDeployments(webService.Id)));

        var nextSpec = request.Body.Spec.ToDomain();
        if (nextSpec.Image is not ImageInfo requestedImage)
            return Error(new WebServiceLifecycleMutationStateError(
                new WebServiceUpdateUnavailable(webService.Id)));

        if (webService.Spec.Image is DeliveredSourceBuildImage delivered)
        {
            if (requestedImage.Server != delivered.Image.Server || requestedImage.Name != delivered.Image.Name ||
                requestedImage.Tag != delivered.Image.Tag)
                return Error(new WebServiceLifecycleMutationStateError(
                    new WebServiceUpdateUnavailable(webService.Id)));

            nextSpec = nextSpec with { Image = delivered };
        }
        else if (webService.Spec.Image is ImageInfo currentImage &&
                 requestedImage.Server == currentImage.Server && requestedImage.Name == currentImage.Name)
        {
            // The owner retains registry authorization when the repository identity is unchanged.
            nextSpec = nextSpec with { Image = requestedImage with { SecretName = currentImage.SecretName } };
        }
        var nextExposure = WebServiceExposure.Create(
            request.Body.Exposure,
            webService.Exposure.GeneratedHostnameLabelPrefix);
        var nextWebService = webService.UpdateDesiredState(
            nextSpec,
            request.Body.Tags,
            nextExposure);
        IReadOnlyDictionary<string, DeploymentTargetPublicIngressDto> publicTargets =
            new Dictionary<string, DeploymentTargetPublicIngressDto>(StringComparer.Ordinal);
        if (webService.Exposure.Mode is WebServiceExposureMode.Private &&
            nextExposure.Mode is WebServiceExposureMode.Public)
        {
            var targetResult = await ResolvePublicTargetsAsync(
                webService,
                deployments,
                cancellationToken);
            if (targetResult is DeploymentTargetPublicIngressAdmissionError targetError)
                return Error(new UpdateWebServiceError(targetError));
            publicTargets = (IReadOnlyDictionary<string, DeploymentTargetPublicIngressDto>)targetResult.Value;
        }
        if (Equals(nextWebService, webService))
        {
            return new UpdateWebServiceOutcome(new UpdateWebServiceResponse(
                webService.Id,
                webService.ActiveRevision,
                deployments));
        }

        var currentQuota = WebServiceDeploymentWorkflow.ToQuotaInput(deployments, webService.Spec);
        var nextQuota = WebServiceDeploymentWorkflow.ToQuotaInput(deployments, nextSpec);
        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<
            WebServiceQuotaInput,
            UpdateWebServiceOutcome>(
            new ExecuteQuotaAdmissionRequest<global::Pole.Features.WebServices.Application.Common.WebServiceQuotaInput,
                global::Pole.Features.WebServices.Application.Endpoints.UpdateWebServiceOutcome>(
                TenantId: webService.TenantId,
                ResourceKey: WebServiceQuotaResourceKey.Create(webService.Id),
                Mutation: new UpdateQuotaMutation<WebServiceQuotaInput>(currentQuota, nextQuota),
                QuotaError: quotaError => Error(quotaError),
                MutationCallback: async (transaction, ct) =>
                {
                    var physicalChanges = !webService.Spec.HasSameConfiguration(nextSpec) ||
                        webService.Exposure.Mode != nextWebService.Exposure.Mode;
                    IReadOnlyList<PlacementSummaryDto> updatedDeployments;
                    if (physicalChanges)
                    {
                        var ingressItemsResult = await CreateExposureApplyItemsAsync(
                            transaction.Session,
                            webService,
                            nextWebService,
                            deployments,
                            publicTargets,
                            ct);
                        if (ingressItemsResult is GeneratedDomainZoneError zonesError)
                        {
                            return transaction.Abort(
                                Error(new UpdateWebServiceError(zonesError)));
                        }
                        if (ingressItemsResult is GeneratedHostnameBindingError bindingsError)
                        {
                            return transaction.Abort(
                                Error(new UpdateWebServiceError(bindingsError)));
                        }
                        var deploymentsResult = await requestResourceDeploymentSetApply(
                            new ResourceDeploymentSetApplyRequest(
                                webService.TenantId,
                                WebServiceResourceDeployment.ResourceType,
                                webService.Id,
                                nextWebService.ActiveRevision,
                                (ResourceDeploymentSetApplyItem[])ingressItemsResult.Value),
                            transaction.Session,
                            ct);
                        if (deploymentsResult is ResourceDeploymentMutationError deploymentError)
                        {
                            return transaction.Abort(
                                Error(new UpdateWebServiceError(deploymentError)));
                        }
                        var physicalDeployments = ((MutatedResourceDeployments)deploymentsResult.Value).Placements;
                        updatedDeployments = physicalDeployments;
                    }
                    else
                    {
                        var deploymentsResult = await advanceResourceDeploymentRevision(
                            new ResourceDeploymentResourceRevisionRequest(
                                webService.TenantId,
                                WebServiceResourceDeployment.ResourceType,
                                webService.Id,
                                nextWebService.ActiveRevision),
                            transaction.Session,
                            ct);
                        if (deploymentsResult is AdvanceResourceDeploymentRevisionError revisionError)
                        {
                            return transaction.Abort(
                                Error(new UpdateWebServiceError(revisionError)));
                        }
                        var revisionDeployments = ((MutatedResourceDeployments)deploymentsResult.Value).Placements;
                        updatedDeployments = revisionDeployments
                            .Where(WebServiceDeploymentWorkflow.IsDesiredPlacement)
                            .ToArray();
                    }

                    var persisted = await persistUpdatedWebService(
                        transaction.Session,
                        webService,
                        nextWebService,
                        nextWebService.ToUpdatedEvent(
                            WebServiceDeploymentWorkflow.ToEventDeployments(updatedDeployments)),
                        ct);
                    if (persisted is WebServiceUpdateConflict persistenceError)
                    {
                        return transaction.Abort(
                            Error(new UpdateWebServiceError(persistenceError)));
                    }
                    if (persisted is WebServiceConnectionConflict connectionConflict)
                    {
                        return transaction.Abort(
                            Error(new UpdateWebServiceError(connectionConflict)));
                    }
                    if (persisted is WebServiceDependencyUnavailable dependencyUnavailable)
                    {
                        return transaction.Abort(
                            Error(new UpdateWebServiceError(dependencyUnavailable)));
                    }

                    var release = WebServiceReleaseFactory.CreatePending(
                        nextWebService.TenantId,
                        nextWebService.Id,
                        nextWebService.ActiveRevision,
                        WebServiceReleaseAction.Update,
                        request.ActorUserId.Value,
                        request.ActorEmail.Value,
                        nextWebService.Spec,
                        WebServiceDeploymentWorkflow.ToReleaseDeployments(updatedDeployments),
                        traceId: null,
                        correlationId: null,
                        new WebServiceReleaseFactory.CreateOptions(
                            MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
                            timeProvider.GetUtcNow().UtcDateTime),
                        exposure: nextWebService.Exposure);
                    await persistCurrentWebServiceRelease(transaction.Session, release, ct);

                    return transaction.Commit(new UpdateWebServiceOutcome(
                        new UpdateWebServiceResponse(
                            nextWebService.Id,
                            nextWebService.ActiveRevision,
                            updatedDeployments)));
                }),
            cancellationToken: cancellationToken);
    }

    private async Task<ResolvePublicTargetsOutcome> ResolvePublicTargetsAsync(
        WebService webService,
        IReadOnlyList<PlacementSummaryDto> deployments,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, DeploymentTargetPublicIngressDto>(StringComparer.Ordinal);
        foreach (var targetId in deployments
                     .Select(deployment => deployment.DeploymentTargetId)
                     .Distinct(StringComparer.Ordinal))
        {
            var result = await admitPublicIngress(
                new DeploymentTargetAdmissionRequest(
                    webService.TenantId,
                    WebServiceResourceDeployment.ResourceType,
                    webService.IsolationScopeId,
                    targetId,
                    DeploymentTargetRequiredCapabilities.PublicIngress),
                cancellationToken);
            if (result is DeploymentTargetPublicIngressDto target)
            {
                resolved.Add(targetId, target);
                continue;
            }
            return result switch
            {
                DeploymentTargetPublicIngressDto => throw new InvalidOperationException(
                    "Public ingress admission returned an inconsistent result."),
                DeploymentTargetNotFound error => new DeploymentTargetPublicIngressAdmissionError(error),
                DeploymentTargetDisabled error => new DeploymentTargetPublicIngressAdmissionError(error),
                DeploymentTargetDeletionRequested error => new DeploymentTargetPublicIngressAdmissionError(error),
                DeploymentTargetNewWorkloadsDisabled error => new DeploymentTargetPublicIngressAdmissionError(error),
                NoDeploymentTargetAcceptsPublicWorkloads error => new DeploymentTargetPublicIngressAdmissionError(error),
                DeploymentTargetSelectionRequired error => new DeploymentTargetPublicIngressAdmissionError(error),
                DeploymentTargetPublicIngressUnavailable error => new DeploymentTargetPublicIngressAdmissionError(error)
            };
        }
        return (IReadOnlyDictionary<string, DeploymentTargetPublicIngressDto>)resolved;
    }

    private async Task<CreateExposureApplyItemsOutcome> CreateExposureApplyItemsAsync(
        MongoDB.Driver.IClientSessionHandle session,
        WebService current,
        WebService updated,
        IReadOnlyList<PlacementSummaryDto> deployments,
        IReadOnlyDictionary<string, DeploymentTargetPublicIngressDto> publicTargets,
        CancellationToken cancellationToken)
    {
        if (current.Exposure.Mode is WebServiceExposureMode.Public &&
            updated.Exposure.Mode is WebServiceExposureMode.Private)
        {
            return deployments.Select(deployment =>
                WebServiceDeploymentWorkflow.ToSetApplyItemForExposure(
                    deployment,
                    new ClearIngress())).ToArray();
        }

        if (current.Exposure.Mode is not WebServiceExposureMode.Private ||
            updated.Exposure.Mode is not WebServiceExposureMode.Public)
        {
            return deployments.Select(deployment =>
                WebServiceDeploymentWorkflow.ToSetApplyItem(deployment)).ToArray();
        }

        var targetIds = deployments
            .Select(deployment => deployment.DeploymentTargetId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var zonesResult = await getOrCreateGeneratedDomainZones(
            targetIds.Select(targetId =>
            {
                var target = publicTargets[targetId];
                return new GetOrCreateGeneratedZoneRequest(
                    updated.TenantId,
                    target.DeploymentTargetId,
                    target.PublicBaseDomain,
                    target.WorkloadConfigurationRevision);
            }).ToArray(),
            session,
            cancellationToken);
        if (zonesResult is GeneratedDomainZoneError zonesError)
            return zonesError;

        var zonesByTarget = ((IReadOnlyList<TenantDomainZoneSnapshot>)zonesResult.Value)
            .ToDictionary(zone => zone.DeploymentTargetId, StringComparer.Ordinal);
        var bindingsByDeployment = new Dictionary<string, HostnameBindingSnapshot>(StringComparer.Ordinal);
        foreach (var deployment in deployments)
        {
            var existingBindings = await loadHostnameBindingsForDeployment(
                new LoadHostnameBindingsForDeploymentInTransactionRequest(
                    TenantId: updated.TenantId,
                    ResourceType: WebServiceResourceDeployment.ResourceType,
                    ResourceId: updated.Id,
                    ResourceDeploymentId: deployment.DeploymentId),
                session: session,
                cancellationToken: cancellationToken);
            var generatedBinding = existingBindings.SingleOrDefault(binding =>
                string.Equals(binding.Kind, HostnameBindingKinds.Generated, StringComparison.Ordinal));
            if (generatedBinding is not null)
                bindingsByDeployment.Add(deployment.DeploymentId, generatedBinding);
        }
        var bindingRequests = deployments
            .Where(deployment => !bindingsByDeployment.ContainsKey(deployment.DeploymentId))
            .Select(deployment =>
            new CreateGeneratedHostnameBindingRequest(
                updated.TenantId,
                zonesByTarget[deployment.DeploymentTargetId].Id,
                deployment.DeploymentTargetId,
                WebServiceResourceDeployment.ResourceType,
                updated.Id,
                deployment.DeploymentId,
                updated.Exposure.GeneratedHostnameLabel(updated.Id))).ToArray();
        if (bindingRequests.Length > 0)
        {
            var bindingsResult = await createGeneratedHostnameBindings(
                bindingRequests,
                session,
                cancellationToken);
            if (bindingsResult is GeneratedHostnameBindingError bindingsError)
                return bindingsError;
            foreach (var binding in (IReadOnlyList<HostnameBindingSnapshot>)bindingsResult.Value)
                bindingsByDeployment.Add(binding.ResourceDeploymentId, binding);
        }

        return deployments.Select(deployment =>
        {
            var target = publicTargets[deployment.DeploymentTargetId];
            var ingress = ResourceDeploymentIngressConfiguration.Create(
                target.IngressClassName,
                target.ClusterIssuerName,
                target.PublicBaseDomain,
                [bindingsByDeployment[deployment.DeploymentId].Hostname]);
            return WebServiceDeploymentWorkflow.ToSetApplyItemForExposure(
                deployment,
                new ReplaceIngress(ingress));
        }).ToArray();
    }

    private union ResolvePublicTargetsOutcome(
        IReadOnlyDictionary<string, DeploymentTargetPublicIngressDto>,
        DeploymentTargetPublicIngressAdmissionError);

    private union CreateExposureApplyItemsOutcome(
        ResourceDeploymentSetApplyItem[],
        GeneratedDomainZoneError,
        GeneratedHostnameBindingError);

    private static UpdateWebServiceOutcome Error(
        UpdateWebServiceError error) =>
        new(error);

}

public sealed class UpdateWebServiceValidator : AbstractValidator<UpdateWebServiceRequest>
{
    public UpdateWebServiceValidator()
    {
        RuleFor(request => request.Id).NotEmpty();
        RuleFor(request => request.Body.Spec)
            .NotNull().WithMessage("Web service specification is required.")
            .SetValidator(new WebServiceSpecDtoValidator());
        RuleFor(request => request.Body.Exposure).IsInEnum();
        RuleFor(request => request.Body.ExpectedRevision).GreaterThan(0).When(request => request.Body.ExpectedRevision is not null);
        RuleFor(request => request)
            .Must(request => CreateWebServiceRequestBodyValidator.HasPublicIngressTarget(
                request.Body.Spec?.Config?.PortMappings))
            .When(request => request.Body.Exposure is WebServiceExposureMode.Public)
            .WithMessage("Public web services require a TCP mapping with a numeric target port.");
    }
}
