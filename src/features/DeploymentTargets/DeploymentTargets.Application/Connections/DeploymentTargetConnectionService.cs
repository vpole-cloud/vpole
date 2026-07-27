using System.Security.Cryptography;
using k8s;
using Pole.Encryption;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Connections;

public interface IDeploymentTargetConnectionPreparer
{
    ValueTask<Result<PreparedDeploymentTargetConnection>> PrepareAsync(
        string deploymentTargetId,
        long connectionVersion,
        DeploymentTargetConnectionInput input,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Recreates a Kubernetes client from a stored deployment-target connection.
/// The caller owns and must dispose a successful client.
/// </summary>
public interface IDeploymentTargetKubernetesClientFactory
{
    ValueTask<Result<IKubernetes>> CreateAsync(
        string deploymentTargetId,
        DeploymentTargetConnection connection,
        CancellationToken cancellationToken = default);
}

public sealed class DeploymentTargetConnectionService(
    IDeploymentTargetConnectionProtector protector) :
    IDeploymentTargetConnectionPreparer,
    IDeploymentTargetKubernetesClientFactory
{
    public async ValueTask<Result<PreparedDeploymentTargetConnection>> PrepareAsync(
        string deploymentTargetId,
        long connectionVersion,
        DeploymentTargetConnectionInput input,
        CancellationToken cancellationToken = default)
    {
        if (!HasValidOwner(deploymentTargetId, connectionVersion))
        {
            return ConnectionFailures.Validation(
                "deploymentTargetId",
                "The deployment-target connection owner is invalid.");
        }

        if (input is null)
        {
            return ConnectionFailures.Validation(
                "connection",
                "A deployment-target connection is required.");
        }

        return input switch
        {
            InClusterConnectionInput => PrepareInCluster(connectionVersion, cancellationToken),
            KubeconfigConnectionInput kubeconfig => await PrepareKubeconfigAsync(
                deploymentTargetId,
                connectionVersion,
                kubeconfig,
                cancellationToken),
            ServiceAccountConnectionInput serviceAccount => await PrepareServiceAccountAsync(
                deploymentTargetId,
                connectionVersion,
                serviceAccount,
                cancellationToken),
            _ => ConnectionFailures.Validation(
                "connection",
                "The deployment-target connection kind is unsupported.")
        };
    }

    public async ValueTask<Result<IKubernetes>> CreateAsync(
        string deploymentTargetId,
        DeploymentTargetConnection connection,
        CancellationToken cancellationToken = default)
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

    private static Result<PreparedDeploymentTargetConnection> PrepareInCluster(
        long connectionVersion,
        CancellationToken cancellationToken)
    {
        var clientResult = KubernetesClientSecurity.CreateInCluster(
            storedConnection: false,
            cancellationToken);
        if (!clientResult.TryGetValue(out var client, out var failure))
            return failure;

        return new PreparedDeploymentTargetConnection(
            DeploymentTargetConnectionFactory.CreateInCluster(connectionVersion),
            client);
    }

    private async ValueTask<Result<PreparedDeploymentTargetConnection>> PrepareKubeconfigAsync(
        string deploymentTargetId,
        long connectionVersion,
        KubeconfigConnectionInput input,
        CancellationToken cancellationToken)
    {
        var canonicalResult = KubeconfigCanonicalizer.Canonicalize(
            input.Kubeconfig,
            input.ContextName,
            cancellationToken);
        if (!canonicalResult.TryGetValue(out var canonical, out var canonicalFailure))
            return canonicalFailure;

        var clientResult = KubernetesClientSecurity.CreateFromCanonical(
            canonical,
            storedConnection: false,
            cancellationToken);
        if (!clientResult.TryGetValue(out var client, out var clientFailure))
            return clientFailure;

        byte[] serialized;
        try
        {
            serialized = KubeconfigCanonicalizer.Serialize(canonical);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            client.Dispose();
            return ConnectionFailures.ConnectionProtectionUnavailable();
        }

        try
        {
            var encryptedResult = await protector.ProtectKubeconfigAsync(
                deploymentTargetId,
                connectionVersion,
                serialized,
                cancellationToken);
            if (!encryptedResult.TryGetValue(out var encrypted, out var encryptionFailure))
            {
                client.Dispose();
                return encryptionFailure;
            }

            var persisted = DeploymentTargetConnectionFactory.CreateKubeconfig(
                connectionVersion,
                canonical.ContextName,
                canonical.ServerUrl,
                encrypted);
            return new PreparedDeploymentTargetConnection(persisted, client);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            client.Dispose();
            return ConnectionFailures.ConnectionProtectionUnavailable();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
        }
    }

    private async ValueTask<Result<PreparedDeploymentTargetConnection>> PrepareServiceAccountAsync(
        string deploymentTargetId,
        long connectionVersion,
        ServiceAccountConnectionInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalResult = KubeconfigCanonicalizer.CreateServiceAccountCredential(
            input.ServerUrl,
            input.CertificateAuthorityData,
            input.Token);
        if (!canonicalResult.TryGetValue(out var canonical, out var canonicalFailure))
            return canonicalFailure;

        var clientResult = KubernetesClientSecurity.CreateFromCanonical(
            canonical,
            storedConnection: false,
            cancellationToken);
        if (!clientResult.TryGetValue(out var client, out var clientFailure))
            return clientFailure;

        byte[] serialized;
        try
        {
            serialized = KubeconfigCanonicalizer.Serialize(canonical);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            client.Dispose();
            return ConnectionFailures.ConnectionProtectionUnavailable();
        }

        try
        {
            var encryptedResult = await protector.ProtectServiceAccountCredentialAsync(
                deploymentTargetId,
                connectionVersion,
                serialized,
                cancellationToken);
            if (!encryptedResult.TryGetValue(out var encrypted, out var encryptionFailure))
            {
                client.Dispose();
                return encryptionFailure;
            }

            var persisted = DeploymentTargetConnectionFactory.CreateServiceAccount(
                connectionVersion,
                canonical.ServerUrl,
                canonical.CertificateAuthorityData,
                encrypted);
            return new PreparedDeploymentTargetConnection(persisted, client);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            client.Dispose();
            return ConnectionFailures.ConnectionProtectionUnavailable();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
        }
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
