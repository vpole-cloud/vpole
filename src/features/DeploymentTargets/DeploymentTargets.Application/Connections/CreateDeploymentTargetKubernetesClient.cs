using System.Security.Cryptography;
using k8s;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Connections;

public sealed record StoredDeploymentTargetConnectionUnavailable;

/// <summary>
/// Recreates a Kubernetes client from a stored deployment-target connection.
/// The caller owns and must dispose a successful client.
/// </summary>
public delegate ValueTask<Result<
    IKubernetes,
    StoredDeploymentTargetConnectionUnavailable>> CreateDeploymentTargetKubernetesClient(
    string deploymentTargetId,
    DeploymentTargetConnection connection,
    CancellationToken cancellationToken = default);

internal sealed class DeploymentTargetKubernetesClientFactory(
    DeploymentTargetConnectionProtector protector)
{
    public async ValueTask<Result<
        IKubernetes,
        StoredDeploymentTargetConnectionUnavailable>> InvokeAsync(
        string deploymentTargetId,
        DeploymentTargetConnection connection,
        CancellationToken cancellationToken = default)
    {
        EnsureValidOwner(deploymentTargetId, connection?.Version ?? 0);

        return connection switch
        {
            InClusterDeploymentTargetConnection =>
                KubernetesClientSecurity.CreateInClusterForStored(cancellationToken),
            KubeconfigDeploymentTargetConnection kubeconfig =>
                await CreateFromStoredKubeconfigAsync(deploymentTargetId, kubeconfig, cancellationToken),
            ServiceAccountDeploymentTargetConnection serviceAccount =>
                await CreateFromStoredServiceAccountAsync(deploymentTargetId, serviceAccount, cancellationToken),
            _ => new Err<StoredDeploymentTargetConnectionUnavailable>(new())
        };
    }

    private async ValueTask<Result<IKubernetes, StoredDeploymentTargetConnectionUnavailable>>
        CreateFromStoredKubeconfigAsync(
        string deploymentTargetId,
        KubeconfigDeploymentTargetConnection connection,
        CancellationToken cancellationToken)
    {
        var decryptedResult = await protector.UnprotectKubeconfigAsync(
            deploymentTargetId,
            connection.Version,
            connection.EncryptedKubeconfig,
            cancellationToken);
        if (!decryptedResult.TryGetValue(out var decrypted, out var decryptionFailure))
            return new Err<StoredDeploymentTargetConnectionUnavailable>(decryptionFailure);

        try
        {
            var canonicalResult = KubeconfigCanonicalizer.DeserializeStored(decrypted);
            if (!canonicalResult.TryGetValue(out var canonical, out var canonicalFailure))
                return new Err<StoredDeploymentTargetConnectionUnavailable>(canonicalFailure);
            if (!string.Equals(canonical.ContextName, connection.ContextName, StringComparison.Ordinal) ||
                !string.Equals(canonical.ServerUrl, connection.ServerUrl, StringComparison.Ordinal))
            {
                return new Err<StoredDeploymentTargetConnectionUnavailable>(new());
            }

            return KubernetesClientSecurity.CreateForStored(
                canonical,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decrypted);
        }
    }

    private async ValueTask<Result<IKubernetes, StoredDeploymentTargetConnectionUnavailable>>
        CreateFromStoredServiceAccountAsync(
        string deploymentTargetId,
        ServiceAccountDeploymentTargetConnection connection,
        CancellationToken cancellationToken)
    {
        var decryptedResult = await protector.UnprotectServiceAccountCredentialAsync(
            deploymentTargetId,
            connection.Version,
            connection.EncryptedCredential,
            cancellationToken);
        if (!decryptedResult.TryGetValue(out var decrypted, out var decryptionFailure))
            return new Err<StoredDeploymentTargetConnectionUnavailable>(decryptionFailure);

        try
        {
            var canonicalResult = KubeconfigCanonicalizer.DeserializeStored(
                decrypted,
                ConnectionPayloadLimits.MaximumServiceAccountCredentialBytes);
            if (!canonicalResult.TryGetValue(out var canonical, out _) ||
                !KubeconfigCanonicalizer.IsServiceAccountCredential(canonical) ||
                !string.Equals(canonical.ServerUrl, connection.ServerUrl, StringComparison.Ordinal) ||
                !string.Equals(
                    canonical.CertificateAuthorityData,
                    connection.CertificateAuthorityData,
                    StringComparison.Ordinal))
            {
                return new Err<StoredDeploymentTargetConnectionUnavailable>(new());
            }

            return KubernetesClientSecurity.CreateForStored(
                canonical,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decrypted);
        }
    }

    private static void EnsureValidOwner(string? deploymentTargetId, long connectionVersion)
    {
        if (string.IsNullOrWhiteSpace(deploymentTargetId) ||
            deploymentTargetId.Length > ConnectionPayloadLimits.MaximumDeploymentTargetIdCharacters ||
            connectionVersion < 1)
        {
            throw new ArgumentException(
                "The stored deployment-target connection owner is invalid.",
                nameof(deploymentTargetId));
        }
    }
}
