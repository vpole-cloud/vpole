using MongoDB.Bson;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence.Reads;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Admission;

internal static class DeploymentTargetAdmissionSupport
{
    public static async Task<Result<DeploymentTarget, DefaultDeploymentTargetLookupError>>
        LoadDefaultAsync(
            LoadDefaultDeploymentTargetId loadDefaultDeploymentTargetId,
            LoadDeploymentTargetById loadDeploymentTargetById,
            CancellationToken cancellationToken)
    {
        var defaultTargetId = await loadDefaultDeploymentTargetId(cancellationToken);
        if (string.IsNullOrWhiteSpace(defaultTargetId))
        {
            return new Err<DefaultDeploymentTargetLookupError>(
                new DefaultDeploymentTargetNotConfigured());
        }

        var deploymentTargetId = NormalizeDeploymentTargetId(
            defaultTargetId,
            "DeploymentTargetSettings.DefaultDeploymentTargetId");
        var target = await loadDeploymentTargetById(deploymentTargetId, cancellationToken);
        return target is null
            ? throw new InvalidOperationException(
                $"Configured default deployment target '{deploymentTargetId}' does not exist.")
            : new Ok<DeploymentTarget>(target);
    }

    public static Result<
        DeploymentTargetLookupDto,
        DeploymentTargetWorkloadAdmissionError> AdmitForWorkload(DeploymentTarget target)
    {
        var lookup = target.ToLookupDto();
        if (!lookup.Enabled)
        {
            return new Err<DeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetWorkloadAdmissionError(
                    new DeploymentTargetDisabled(lookup.Id)));
        }
        if (lookup.DeletionRequested)
        {
            return new Err<DeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetWorkloadAdmissionError(
                    new DeploymentTargetDeletionRequested(lookup.Id)));
        }
        if (!lookup.AcceptsWorkloads)
        {
            return new Err<DeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetWorkloadAdmissionError(
                    new DeploymentTargetNewWorkloadsDisabled(lookup.Id)));
        }

        return new Ok<DeploymentTargetLookupDto>(lookup);
    }

    public static Result<
        DeploymentTargetLookupDto,
        DefaultDeploymentTargetWorkloadAdmissionError> AdmitDefaultForNewWorkload(
        DeploymentTarget target)
    {
        var lookup = target.ToLookupDto();
        if (!lookup.Enabled)
            return new Err<DefaultDeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetDisabled(lookup.Id));
        if (lookup.DeletionRequested)
            return new Err<DefaultDeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetDeletionRequested(lookup.Id));
        if (!lookup.AcceptsWorkloads)
            return new Err<DefaultDeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetNewWorkloadsDisabled(lookup.Id));

        return new Ok<DeploymentTargetLookupDto>(lookup);
    }

    public static bool HasRequiredCapabilities(
        DeploymentTargetLookupDto target,
        DeploymentTargetRequiredCapabilities requiredCapabilities) =>
        (!requiredCapabilities.HasFlag(DeploymentTargetRequiredCapabilities.PublicIngress) ||
         target.WorkloadConfiguration.Ingress?.PublicBaseDomain is not null) &&
        (!requiredCapabilities.HasFlag(
             DeploymentTargetRequiredCapabilities.FunctionRuntimeConnectivity) ||
         string.Equals(
             target.ConnectionKind,
             "InCluster",
             StringComparison.Ordinal));

    public static DeploymentTargetPublicIngressDto ToPublicIngressDto(
        DeploymentTargetLookupDto target)
    {
        var ingress = target.WorkloadConfiguration.Ingress!;
        return new DeploymentTargetPublicIngressDto(
            target.Id,
            target.WorkloadConfiguration.Revision,
            ingress.IngressClassName,
            ingress.ClusterIssuerName,
            ingress.PublicBaseDomain!);
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
