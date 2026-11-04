using System.Collections.Immutable;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;

namespace Pole.Features.WebServices.Application.Common;

/// <summary>
/// Maps the contract-owned physical deployment snapshots into the logical WebService
/// workflow. It deliberately performs no I/O: handlers resolve/fence providers in bulk,
/// then use these deterministic values for quota, releases, and exact mutations.
/// </summary>
internal static class WebServiceDeploymentWorkflow
{
    public static bool IsDesiredPlacement(PlacementSummaryDto deployment) =>
        deployment.DesiredOperation != ResourceDeploymentDesiredOperation.Delete &&
        deployment.Status != ResourceDeploymentStatus.Deleted;

    public static WebServiceQuotaInput ToQuotaInput(
        IEnumerable<PlacementSummaryDto> deployments,
        WebServiceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(deployments);
        ArgumentNullException.ThrowIfNull(spec);

        return WebServiceQuotaInput.Create(deployments
            .Where(IsDesiredPlacement)
            .Select(deployment => WebServiceDeploymentQuotaInput.Create(
                deployment.Compute.Requests.CpuMillis,
                deployment.Compute.Requests.MemoryMi,
                spec.Autoscaling?.MaxReplicas ?? deployment.FixedReplicas)));
    }

    public static WebServiceQuotaInput ToQuotaInput(
        IEnumerable<(ResolvedComputeProfile Compute, int FixedReplicas)> deployments,
        WebServiceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(deployments);
        ArgumentNullException.ThrowIfNull(spec);

        return WebServiceQuotaInput.Create(deployments.Select(deployment =>
            WebServiceDeploymentQuotaInput.Create(
                deployment.Compute.Requests.CpuMillis,
                deployment.Compute.Requests.MemoryMi,
                spec.Autoscaling?.MaxReplicas ?? deployment.FixedReplicas)));
    }

    public static WebServiceQuotaInput ToQuotaInputWithAdditionalDeployment(
        IEnumerable<PlacementSummaryDto> currentDeployments,
        WebServiceSpec spec,
        ResolvedComputeProfile compute,
        int fixedReplicas)
    {
        ArgumentNullException.ThrowIfNull(currentDeployments);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(compute);

        var deployments = currentDeployments
            .Where(IsDesiredPlacement)
            .Select(deployment => WebServiceDeploymentQuotaInput.Create(
                deployment.Compute.Requests.CpuMillis,
                deployment.Compute.Requests.MemoryMi,
                spec.Autoscaling?.MaxReplicas ?? deployment.FixedReplicas))
            .Append(WebServiceDeploymentQuotaInput.Create(
                compute.Requests.CpuMillis,
                compute.Requests.MemoryMi,
                spec.Autoscaling?.MaxReplicas ?? fixedReplicas));
        return WebServiceQuotaInput.Create(deployments);
    }

    public static ImmutableList<WebServiceReleaseDeployment> ToReleaseDeployments(
        IEnumerable<PlacementSummaryDto> deployments,
        WebServiceReleaseDeploymentOperation operation = WebServiceReleaseDeploymentOperation.Apply) =>
        deployments
            .OrderBy(deployment => deployment.DeploymentId, StringComparer.Ordinal)
            .Select(deployment => new WebServiceReleaseDeployment(
                deployment.DeploymentId,
                deployment.DeploymentTargetId,
                deployment.ResourceRevision,
                deployment.DesiredGeneration,
                operation,
                InitialOutcome(deployment, operation),
                CompletedAtUtc: null,
                FailureCode: null,
                FailureMessage: null))
            .ToImmutableList();

    public static ImmutableList<WebServiceDesiredDeploymentSnapshot> ToEventDeployments(
        IEnumerable<PlacementSummaryDto> deployments,
        WebServiceReleaseDeploymentOperation operation = WebServiceReleaseDeploymentOperation.Apply) =>
        deployments
            .OrderBy(deployment => deployment.DeploymentId, StringComparer.Ordinal)
            .Select(deployment => new WebServiceDesiredDeploymentSnapshot(
                deployment.DeploymentId,
                deployment.DeploymentTargetId,
                deployment.ResourceRevision,
                deployment.DesiredGeneration,
                operation.ToString(),
                deployment.Ingress?.Hostnames ?? ImmutableList<string>.Empty))
            .ToImmutableList();

    public static ResourceDeploymentSetApplyItem ToSetApplyItem(
        PlacementSummaryDto deployment,
        ResourceDeploymentComputeConfiguration? replacementCompute = null,
        ResourceDeploymentIngressConfiguration? replacementIngress = null,
        int? fixedReplicas = null) =>
        new(
            deployment.DeploymentId,
            deployment.DeploymentRevision,
            deployment.Compute.Profile,
            replacementIngress is null
                ? new PreserveIngress()
                : new ReplaceIngress(replacementIngress),
            replacementCompute,
            fixedReplicas);

    public static ResourceDeploymentSetApplyItem ToSetApplyItemForExposure(
        PlacementSummaryDto deployment,
        ResourceDeploymentIngressChange ingressChange) =>
        new(
            deployment.DeploymentId,
            deployment.DeploymentRevision,
            deployment.Compute.Profile,
            ingressChange);

    public static ResourceDeploymentExactMutationRequest ToExactApplyRequest(
        string tenantId,
        string resourceId,
        PlacementSummaryDto deployment,
        long resourceRevision,
        ResourceDeploymentComputeConfiguration? replacementCompute = null,
        ResourceDeploymentIngressConfiguration? replacementIngress = null,
        int? fixedReplicas = null) =>
        new(
            tenantId,
            WebServiceResourceDeployment.ResourceType,
            resourceId,
            deployment.DeploymentId,
            deployment.DeploymentRevision,
            deployment.Compute.Profile,
            replacementCompute,
            replacementIngress,
            fixedReplicas,
            resourceRevision);

    public static ResourceDeploymentIngressConfiguration WithHostnames(
        PlacementSummaryDto deployment,
        IEnumerable<string> hostnames)
    {
        ArgumentNullException.ThrowIfNull(hostnames);
        var ingress = deployment.Ingress ?? throw new InvalidOperationException(
            "A public web service deployment must have a resolved ingress snapshot.");
        return ResourceDeploymentIngressConfiguration.Create(
            ingress.IngressClassName,
            ingress.ClusterIssuerName,
            ingress.PublicBaseDomain,
            hostnames.Order(StringComparer.Ordinal));
    }

    private static WebServiceReleaseDeploymentOutcome InitialOutcome(
        PlacementSummaryDto deployment,
        WebServiceReleaseDeploymentOperation operation)
    {
        if (operation == WebServiceReleaseDeploymentOperation.Delete)
        {
            return deployment.Status == ResourceDeploymentStatus.Deleted
                ? WebServiceReleaseDeploymentOutcome.Removed
                : WebServiceReleaseDeploymentOutcome.Pending;
        }

        return deployment.Status switch
        {
            ResourceDeploymentStatus.Applied when deployment.ObservedGeneration >= deployment.DesiredGeneration =>
                WebServiceReleaseDeploymentOutcome.Succeeded,
            ResourceDeploymentStatus.Failed when !string.IsNullOrWhiteSpace(deployment.FailureCode) =>
                WebServiceReleaseDeploymentOutcome.Failed,
            _ => WebServiceReleaseDeploymentOutcome.Pending
        };
    }
}
