using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Operations.Deployments;

public sealed record AddWebServiceDeploymentCommand(
    string TenantId,
    string WebServiceId,
    string DeploymentTargetId,
    int FixedReplicas,
    string? ComputeProfileId,
    string? ActorUserId,
    string? ActorEmail);

public sealed class AddWebServiceDeploymentOperation(
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
{
    public async Task<Result<PlacementSummaryDto>> ExecuteAsync(
        AddWebServiceDeploymentCommand command,
        CancellationToken cancellationToken = default)
    {
        var stateResult = await LoadCurrentStateAsync(command, cancellationToken);
        if (!stateResult.TryGetValue(out var state, out var stateFailure))
            return stateFailure;

        var placementResult = await ResolvePlacementAsync(
            command,
            state.WebService,
            cancellationToken);
        if (!placementResult.TryGetValue(out var placement, out var placementFailure))
            return placementFailure;

        var currentQuota = WebServiceDeploymentWorkflow.ToQuotaInput(
            state.Deployments,
            state.WebService.Spec);
        var nextQuota = WebServiceDeploymentWorkflow.ToQuotaInputWithAdditionalDeployment(
            state.Deployments,
            state.WebService.Spec,
            placement.Compute,
            command.FixedReplicas);

        return await quotaAdmissionExecutor.ExecuteAsync<
            WebServiceQuotaInput,
            PlacementSummaryDto>(
            state.WebService.TenantId,
            WebServiceQuotaResourceKey.Create(state.WebService.Id),
            new UpdateQuotaMutation<WebServiceQuotaInput>(currentQuota, nextQuota),
            (session, ct) => PersistAsync(command, state, placement, session, ct),
            cancellationToken);
    }

    private async Task<Result<CurrentState>> LoadCurrentStateAsync(
        AddWebServiceDeploymentCommand command,
        CancellationToken cancellationToken)
    {
        var webService = await loadWebServiceById(
            command.TenantId,
            command.WebServiceId,
            cancellationToken);
        if (webService is null)
            return Failures.NotFound("Web service not found.", command.WebServiceId);
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Failures.Conflict("A deleting web service cannot be updated.");

        var deployments = await deploymentQuery.GetAllAsync(
            command.TenantId,
            WebServiceResourceDeployment.ResourceType,
            command.WebServiceId,
            cancellationToken);
        if (deployments.Any(deployment => string.Equals(
                deployment.DeploymentTargetId,
                command.DeploymentTargetId,
                StringComparison.Ordinal)))
        {
            return Failures.Conflict(
                "The web service already has a deployment on the selected target.");
        }

        return new CurrentState(webService, deployments);
    }

    private async Task<Result<ResolvedAddition>> ResolvePlacementAsync(
        AddWebServiceDeploymentCommand command,
        WebService webService,
        CancellationToken cancellationToken)
    {
        var targetResult = await targetLookup.ResolveForNewWorkloadAsync(
            new DeploymentTargetAdmissionRequest(
                command.TenantId,
                WebServiceResourceDeployment.ResourceType,
                webService.IsolationScopeId,
                command.DeploymentTargetId,
                DeploymentTargetRequiredCapabilities.PublicIngress),
            cancellationToken);
        if (!targetResult.TryGetValue(out var target, out var targetFailure))
            return targetFailure;

        var ingress = target.WorkloadConfiguration.Ingress;
        if (ingress?.PublicBaseDomain is null)
        {
            throw new InvalidOperationException(
                "Public-ingress target admission returned a target without a public base domain.");
        }

        var computeResult = await resolveComputeProfiles(
            new ComputeProfileBatchResolutionRequest(
                command.TenantId,
                WebServiceResourceDeployment.ResourceType,
                [new ComputeProfileTargetResolutionRequest(
                    target.Id,
                    command.ComputeProfileId)]),
            cancellationToken);
        return computeResult.TryGetValue(out var computes, out var computeFailure)
            ? new ResolvedAddition(target, ingress, computes.Single())
            : computeFailure;
    }

    private async Task<Result<PlacementSummaryDto>> PersistAsync(
        AddWebServiceDeploymentCommand command,
        CurrentState state,
        ResolvedAddition placement,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var webService = state.WebService;
        var updatedWebService = webService.AdvancePlacementDesiredState();
        var deploymentId = ObjectId.GenerateNewId().ToString();
        var zoneResult = await getOrCreateGeneratedDomainZone(
            new GetOrCreateGeneratedZoneRequest(
                webService.TenantId,
                placement.Target.Id,
                placement.Ingress.PublicBaseDomain!,
                placement.Target.WorkloadConfiguration.Revision),
            session,
            cancellationToken);
        if (!zoneResult.TryGetValue(out var zone, out var zoneFailure))
            return zoneFailure;

        var bindingResult = await createGeneratedHostnameBinding(
            new CreateGeneratedHostnameBindingRequest(
                webService.TenantId,
                zone.Id,
                placement.Target.Id,
                WebServiceResourceDeployment.ResourceType,
                webService.Id,
                deploymentId,
                $"{webService.Exposure.GeneratedHostnameLabelPrefix}-{webService.Id}"),
            session,
            cancellationToken);
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
                        placement.Target.Id,
                        placement.Target.WorkloadConfiguration.Revision,
                        ResourceDeploymentIngressConfiguration.Create(
                            placement.Ingress.IngressClassName,
                            placement.Ingress.ClusterIssuerName,
                            placement.Ingress.PublicBaseDomain,
                            [binding.Hostname])),
                    placement.Compute.ToResourceDeploymentConfiguration(),
                    Storage: null,
                    ResourcePlacementCapability.MultiTarget,
                    command.FixedReplicas,
                    updatedWebService.ActiveRevision)
            ]),
            session,
            cancellationToken);
        if (!created.TryGetValue(out var createdDeployments, out var createdFailure))
            return createdFailure;

        var advanced = await deploymentWriter.AdvanceResourceRevisionAsync(
            new ResourceDeploymentResourceRevisionRequest(
                webService.TenantId,
                WebServiceResourceDeployment.ResourceType,
                webService.Id,
                updatedWebService.ActiveRevision),
            session,
            cancellationToken);
        if (!advanced.TryGetValue(out var allDeployments, out var advancedFailure))
            return advancedFailure;

        var persisted = await persistUpdatedWebService(
            session,
            webService,
            updatedWebService,
            updatedWebService.ToUpdatedEvent(
                WebServiceDeploymentWorkflow.ToEventDeployments(allDeployments)),
            cancellationToken);
        if (persisted is not ISuccess)
            return (Failure)persisted;

        var release = WebServiceReleaseFactory.CreatePending(
            webService.TenantId,
            webService.Id,
            updatedWebService.ActiveRevision,
            WebServiceReleaseAction.AddDeployment,
            command.ActorUserId,
            command.ActorEmail,
            updatedWebService.Spec,
            WebServiceDeploymentWorkflow.ToReleaseDeployments(allDeployments),
            null,
            null,
            new WebServiceReleaseFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                timeProvider.GetUtcNow().UtcDateTime));
        await persistCurrentWebServiceRelease(session, release, cancellationToken);
        return createdDeployments.Single();
    }

    private sealed record CurrentState(
        WebService WebService,
        IReadOnlyList<PlacementSummaryDto> Deployments);

    private sealed record ResolvedAddition(
        DeploymentTargetLookupDto Target,
        DeploymentTargetIngressConfigurationLookupDto Ingress,
        ResolvedComputeProfile Compute);
}
