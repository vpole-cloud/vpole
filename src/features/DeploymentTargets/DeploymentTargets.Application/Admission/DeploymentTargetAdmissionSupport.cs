using MongoDB.Bson;
using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Catalog;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Admission;

internal union LoadDefaultDeploymentTargetOutcome(
    DeploymentTarget,
    DefaultDeploymentTargetLookupError);

internal static class DeploymentTargetAdmissionSupport
{
    public static async Task<LoadDefaultDeploymentTargetOutcome>
        LoadDefaultAsync(
            LoadDefaultDeploymentTargetId loadDefaultDeploymentTargetId,
            LoadDeploymentTargetById loadDeploymentTargetById,
            CancellationToken cancellationToken)
    {
        var defaultTargetId = await loadDefaultDeploymentTargetId(cancellationToken);
        if (string.IsNullOrWhiteSpace(defaultTargetId))
        {
            return new DefaultDeploymentTargetLookupError(
                new DefaultDeploymentTargetNotConfigured());
        }

        var deploymentTargetId = NormalizeDeploymentTargetId(
            defaultTargetId,
            "DeploymentTargetSettings.DefaultDeploymentTargetId");
        var target = await loadDeploymentTargetById(deploymentTargetId, cancellationToken);
        return target is null
            ? throw new InvalidOperationException(
                $"Configured default deployment target '{deploymentTargetId}' does not exist.")
            : target;
    }

    public static AdmitDeploymentTargetForWorkloadOutcome AdmitForWorkload(DeploymentTarget target)
    {
        var lookup = target.ToLookupDto();
        if (!lookup.Enabled)
        {
            return new DeploymentTargetWorkloadAdmissionError(
                new DeploymentTargetDisabled(lookup.Id));
        }
        if (lookup.DeletionRequested)
        {
            return new DeploymentTargetWorkloadAdmissionError(
                new DeploymentTargetDeletionRequested(lookup.Id));
        }
        if (!lookup.AcceptsWorkloads || !target.WorkloadConfiguration.NetworkPolicy.AllowsWorkloads(target))
        {
            return new DeploymentTargetWorkloadAdmissionError(
                new DeploymentTargetNewWorkloadsDisabled(lookup.Id));
        }

        return lookup;
    }

    public static AdmitDefaultDeploymentTargetForNewWorkloadOutcome AdmitDefaultForNewWorkload(
        DeploymentTarget target)
    {
        var lookup = target.ToLookupDto();
        if (!lookup.Enabled)
            return new DefaultDeploymentTargetWorkloadAdmissionError(
                new DeploymentTargetDisabled(lookup.Id));
        if (lookup.DeletionRequested)
            return new DefaultDeploymentTargetWorkloadAdmissionError(
                new DeploymentTargetDeletionRequested(lookup.Id));
        if (!lookup.AcceptsWorkloads || !target.WorkloadConfiguration.NetworkPolicy.AllowsWorkloads(target))
            return new DefaultDeploymentTargetWorkloadAdmissionError(
                new DeploymentTargetNewWorkloadsDisabled(lookup.Id));

        return lookup;
    }

    public static bool HasRequiredCapabilities(
        DeploymentTargetLookupDto target,
        DeploymentTargetRequiredCapabilities requiredCapabilities,
        string? requiredNetworkSource = null) => (!requiredCapabilities.HasFlag(DeploymentTargetRequiredCapabilities.PublicIngress) ||
         target.WorkloadConfiguration.Ingress?.PublicBaseDomain is not null &&
         target.WorkloadConfiguration.NetworkSources.Get(DeploymentTargetNetworkSourcesDto.PublicIngressName) is not null) &&
        (requiredNetworkSource is null ||
         target.WorkloadConfiguration.NetworkSources.Get(requiredNetworkSource) is not null);

    public static DeploymentTargetPublicIngressDto ToPublicIngressDto(
        DeploymentTargetLookupDto target)
    {
        var ingress = target.WorkloadConfiguration.Ingress!;
        return new DeploymentTargetPublicIngressDto(
            target.Id,
            target.WorkloadConfiguration.Revision,
            ingress.IngressClassName,
            ingress.ClusterIssuerName,
            ingress.PublicBaseDomain!,
            target.WorkloadConfiguration.NetworkSources.Get(DeploymentTargetNetworkSourcesDto.PublicIngressName)!);
    }

    public static string NormalizeDeploymentTargetId(
        string? deploymentTargetId,
        string parameterName = "deploymentTargetId")
    {
        var normalizedDeploymentTargetId = deploymentTargetId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedDeploymentTargetId))
        {
            throw new ArgumentException(
                "A deployment target ID is required.",
                parameterName);
        }
        if (!ObjectId.TryParse(normalizedDeploymentTargetId, out _))
        {
            throw new ArgumentException(
                "The deployment target ID must be a MongoDB ObjectId.",
                parameterName);
        }

        return normalizedDeploymentTargetId;
    }
}
