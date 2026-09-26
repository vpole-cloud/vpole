using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

internal static class DeploymentTargetConnectionRequestBodyExtensions
{
    public static DeploymentTargetConnectionInput ToInput(
        this DeploymentTargetConnectionRequestBody body) => Enum.Parse<DeploymentTargetConnectionKind>(body.Kind, ignoreCase: true) switch
        {
            DeploymentTargetConnectionKind.InCluster => new InClusterConnectionInput(),
            DeploymentTargetConnectionKind.Kubeconfig => new KubeconfigConnectionInput(body.Kubeconfig!, body.ContextName),
            DeploymentTargetConnectionKind.ServiceAccount => new ServiceAccountConnectionInput(
                    body.ServerUrl!,
                    body.CertificateAuthorityData!,
                    body.Token!),
            _ => throw new InvalidOperationException("Unknown deployment-target connection kind.")
        };
}
