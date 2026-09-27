using Pole.Features.DeploymentTargets.Contracts;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Admission;

internal sealed class MongoAdmitDeploymentTargetPublicIngressForDomainReservation(
    AdmitDeploymentTargetForWorkload admitDeploymentTargetForWorkload)
{
    public async Task<AdmitDeploymentTargetPublicIngressForDomainReservationOutcome> InvokeAsync(
        string tenantId,
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var admitted = await admitDeploymentTargetForWorkload(
            deploymentTargetId,
            cancellationToken);
        if (admitted is DeploymentTargetWorkloadAdmissionError admissionError)
        {
            return admissionError switch
            {
                DeploymentTargetNotFound error => new DeploymentTargetDomainReservationAdmissionError(error),
                DeploymentTargetDisabled error => new DeploymentTargetDomainReservationAdmissionError(error),
                DeploymentTargetDeletionRequested error => new DeploymentTargetDomainReservationAdmissionError(error),
                DeploymentTargetNewWorkloadsDisabled error => new DeploymentTargetDomainReservationAdmissionError(error)
            };
        }
        var target = (DeploymentTargetLookupDto)admitted.Value;

        return DeploymentTargetAdmissionSupport.HasRequiredCapabilities(
            target,
            DeploymentTargetRequiredCapabilities.PublicIngress)
            ? DeploymentTargetAdmissionSupport.ToPublicIngressDto(target)
            : new DeploymentTargetDomainReservationAdmissionError(
                new DeploymentTargetPublicIngressUnavailable(target.Id));
    }
}
