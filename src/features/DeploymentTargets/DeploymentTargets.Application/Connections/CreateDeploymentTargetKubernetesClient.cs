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
        var client = await CreateCoreAsync(
            deploymentTargetId,
            connection,
            cancellationToken);
        return client switch
        {
            Success<IKubernetes> success => new Ok<IKubernetes>(success.Value),
            Failure<IKubernetes> => new Err<StoredDeploymentTargetConnectionUnavailable>(
                new StoredDeploymentTargetConnectionUnavailable()),
            _ => throw new InvalidOperationException(
                "Creating a deployment-target Kubernetes client returned an unsupported result case.")
        };
    }

    private async ValueTask<Result<IKubernetes>> CreateCoreAsync(
        string deploymentTargetId,
        DeploymentTargetConnection connection,
        CancellationToken cancellationToken)
    {
        if (connection is null || !HasValidOwner(deploymentTargetId, connection.Version))
            return ConnectionFailures.StoredConnectionUnavailable();

        cancellationToken.ThrowIfCancellationRequested();
        return connection switch
        {
            InClusterDeploymentTargetConnection =>
                KubernetesClientSecurity.CreateInCluster(storedConnection: true, cancellationToken),
            KubeconfigDeploymentTargetConnection kubeconfig =>
                await CreateFromStoredKubeconfigAsync(deploymentTargetId, kubeconfig, cancellationToken),
            ServiceAccountDeploymentTargetConnection serviceAccount =>
                await CreateFromStoredServiceAccountAsync(deploymentTargetId, serviceAccount, cancellationToken),
            _ => ConnectionFailures.StoredConnectionUnavailable()
        };
    }

    private async ValueTask<Result<IKubernetes>> CreateFromStoredKubeconfigAsync(
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
            return decryptionFailure;

        try
        {
            var canonicalResult = KubeconfigCanonicalizer.DeserializeStored(decrypted);
            if (!canonicalResult.TryGetValue(out var canonical, out var canonicalFailure))
                return canonicalFailure;
            if (!string.Equals(canonical.ContextName, connection.ContextName, StringComparison.Ordinal) ||
                !string.Equals(canonical.ServerUrl, connection.ServerUrl, StringComparison.Ordinal))
            {
                return ConnectionFailures.StoredConnectionUnavailable();
            }

            return KubernetesClientSecurity.CreateFromCanonical(
                canonical,
                storedConnection: true,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decrypted);
        }
    }

    private async ValueTask<Result<IKubernetes>> CreateFromStoredServiceAccountAsync(
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
            return decryptionFailure;

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
                return ConnectionFailures.StoredConnectionUnavailable();
            }

            return KubernetesClientSecurity.CreateFromCanonical(
                canonical,
                storedConnection: true,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decrypted);
        }
    }

    private static bool HasValidOwner(string? deploymentTargetId, long connectionVersion) =>
        !string.IsNullOrWhiteSpace(deploymentTargetId) &&
        deploymentTargetId.Length <= ConnectionPayloadLimits.MaximumDeploymentTargetIdCharacters &&
        connectionVersion > 0;
}
