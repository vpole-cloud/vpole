using System.Security.Cryptography;
using k8s;
using Pole.Encryption;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Connections;

public union PrepareDeploymentTargetConnectionOutcome(
    PreparedDeploymentTargetConnection,
    DeploymentTargetConnectionRejected);

public delegate ValueTask<PrepareDeploymentTargetConnectionOutcome> PrepareDeploymentTargetConnection(
    string deploymentTargetId,
    long connectionVersion,
    DeploymentTargetConnectionInput input,
    CancellationToken cancellationToken = default);

internal sealed class DeploymentTargetConnectionPreparer(
    DeploymentTargetConnectionProtector protector
)
{
    public async ValueTask<PrepareDeploymentTargetConnectionOutcome> InvokeAsync(
        string deploymentTargetId,
        long connectionVersion,
        DeploymentTargetConnectionInput input,
        CancellationToken cancellationToken = default)
    {
        EnsureValidOwner(deploymentTargetId, connectionVersion);
        if (input is null)
            return ConnectionRejections.Validation("connection", "A deployment-target connection is required.");

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
            _ => ConnectionRejections.Validation("connection", "The deployment-target connection kind is unsupported."),
        };
    }

    private static PrepareDeploymentTargetConnectionOutcome PrepareInCluster(
        long connectionVersion,
        CancellationToken cancellationToken)
    {
        var clientResult = KubernetesClientSecurity.CreateInClusterForCandidate(cancellationToken);
        if (clientResult is DeploymentTargetConnectionRejected failure)
            return failure;
        var client = (IKubernetes)clientResult.Value;

        return new PreparedDeploymentTargetConnection(
            DeploymentTargetConnectionFactory.CreateInCluster(connectionVersion),
            client);
    }

    private async ValueTask<PrepareDeploymentTargetConnectionOutcome> PrepareKubeconfigAsync(
        string deploymentTargetId,
        long connectionVersion,
        KubeconfigConnectionInput input,
        CancellationToken cancellationToken)
    {
        var canonicalResult = KubeconfigCanonicalizer.Canonicalize(
            input.Kubeconfig,
            input.ContextName,
            cancellationToken);
        if (canonicalResult is DeploymentTargetConnectionRejected canonicalFailure)
            return canonicalFailure;
        var canonical = (CanonicalKubeconfigCredential)canonicalResult.Value;

        var clientResult = KubernetesClientSecurity.CreateForCandidate(canonical, cancellationToken);
        if (clientResult is DeploymentTargetConnectionRejected clientFailure)
            return clientFailure;
        var client = (IKubernetes)clientResult.Value;

        byte[] serialized;
        try
        {
            serialized = KubeconfigCanonicalizer.Serialize(canonical);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            client.Dispose();
            throw new InvalidOperationException("The deployment-target connection could not be serialized.", exception);
        }

        try
        {
            var encryptedResult = await protector.ProtectKubeconfigAsync(
                deploymentTargetId,
                connectionVersion,
                serialized,
                cancellationToken);
            if (encryptedResult is DeploymentTargetConnectionRejected encryptionFailure)
            {
                client.Dispose();
                return encryptionFailure;
            }

            var encrypted = (EncryptedValue)encryptedResult.Value;

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
            throw new InvalidOperationException("The deployment-target connection could not be protected.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
        }
    }

    private async ValueTask<PrepareDeploymentTargetConnectionOutcome> PrepareServiceAccountAsync(
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
        if (canonicalResult is DeploymentTargetConnectionRejected canonicalFailure)
            return canonicalFailure;
        var canonical = (CanonicalKubeconfigCredential)canonicalResult.Value;

        var clientResult = KubernetesClientSecurity.CreateForCandidate(canonical, cancellationToken);
        if (clientResult is DeploymentTargetConnectionRejected clientFailure)
            return clientFailure;
        var client = (IKubernetes)clientResult.Value;

        byte[] serialized;
        try
        {
            serialized = KubeconfigCanonicalizer.Serialize(canonical);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            client.Dispose();
            throw new InvalidOperationException("The deployment-target connection could not be serialized.", exception);
        }

        try
        {
            var encryptedResult = await protector.ProtectServiceAccountCredentialAsync(
                deploymentTargetId,
                connectionVersion,
                serialized,
                cancellationToken);
            if (encryptedResult is DeploymentTargetConnectionRejected encryptionFailure)
            {
                client.Dispose();
                return encryptionFailure;
            }

            var encrypted = (EncryptedValue)encryptedResult.Value;

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
            throw new InvalidOperationException("The deployment-target connection could not be protected.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
        }
    }

    private static void EnsureValidOwner(string? deploymentTargetId, long connectionVersion)
    {
        if (string.IsNullOrWhiteSpace(deploymentTargetId) ||
            deploymentTargetId.Length > ConnectionPayloadLimits.MaximumDeploymentTargetIdCharacters ||
            connectionVersion < 1)
            throw new ArgumentException(
                "The deployment-target connection owner is invalid.",
                nameof(deploymentTargetId));
    }
}
