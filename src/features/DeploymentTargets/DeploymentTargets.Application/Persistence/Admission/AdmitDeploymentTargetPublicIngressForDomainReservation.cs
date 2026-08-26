using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Admission;

internal sealed class MongoAdmitDeploymentTargetPublicIngressForDomainReservation(
    AdmitDeploymentTargetForWorkload admitDeploymentTargetForWorkload)
{
    public async Task<Result<
        DeploymentTargetPublicIngressDto,
        DeploymentTargetDomainReservationAdmissionError>> InvokeAsync(
        string tenantId,
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var admitted = await admitDeploymentTargetForWorkload(
            deploymentTargetId,
            cancellationToken);
        if (!admitted.TryGetValue(out var target, out var admissionError))
        {
            return admissionError switch
            {
                DeploymentTargetNotFound error =>
                    new Err<DeploymentTargetDomainReservationAdmissionError>(error),
                DeploymentTargetDisabled error =>
                    new Err<DeploymentTargetDomainReservationAdmissionError>(error),
                DeploymentTargetDeletionRequested error =>
                    new Err<DeploymentTargetDomainReservationAdmissionError>(error),
                DeploymentTargetNewWorkloadsDisabled error =>
                    new Err<DeploymentTargetDomainReservationAdmissionError>(error)
            };
        }

        return DeploymentTargetAdmissionSupport.HasRequiredCapabilities(
            target,
            DeploymentTargetRequiredCapabilities.PublicIngress)
            ? new Ok<DeploymentTargetPublicIngressDto>(
                DeploymentTargetAdmissionSupport.ToPublicIngressDto(target))
            : new Err<DeploymentTargetDomainReservationAdmissionError>(
                new DeploymentTargetPublicIngressUnavailable(target.Id));
    }
}
