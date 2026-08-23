using System.Security.Cryptography;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Connections;

public delegate ValueTask<Result<
    PreparedDeploymentTargetConnection,
    DeploymentTargetConnectionRejected>>
    PrepareDeploymentTargetConnection(
        string deploymentTargetId,
        long connectionVersion,
        DeploymentTargetConnectionInput input,
        CancellationToken cancellationToken = default);

internal sealed class DeploymentTargetConnectionPreparer(
    DeploymentTargetConnectionProtector protector)
{
    public async ValueTask<Result<
        PreparedDeploymentTargetConnection,
        DeploymentTargetConnectionRejected>> InvokeAsync(
        string deploymentTargetId,
        long connectionVersion,
        DeploymentTargetConnectionInput input,
        CancellationToken cancellationToken = default)
    {
        EnsureValidOwner(deploymentTargetId, connectionVersion);
        if (input is null)
        {
            return new Err<DeploymentTargetConnectionRejected>(
                ConnectionRejections.Validation(
                    "connection",
                    "A deployment-target connection is required."));
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
            _ => new Err<DeploymentTargetConnectionRejected>(
                ConnectionRejections.Validation(
                    "connection",
                    "The deployment-target connection kind is unsupported."))
        };
    }

    private static Result<PreparedDeploymentTargetConnection, DeploymentTargetConnectionRejected> PrepareInCluster(
        long connectionVersion,
        CancellationToken cancellationToken)
    {
        var clientResult = KubernetesClientSecurity.CreateInClusterForCandidate(cancellationToken);
        if (!clientResult.TryGetValue(out var client, out var failure))
            return new Err<DeploymentTargetConnectionRejected>(failure);

        return new Ok<PreparedDeploymentTargetConnection>(
            new PreparedDeploymentTargetConnection(
                DeploymentTargetConnectionFactory.CreateInCluster(connectionVersion),
                client));
    }

    private async ValueTask<Result<
        PreparedDeploymentTargetConnection,
        DeploymentTargetConnectionRejected>> PrepareKubeconfigAsync(
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
            return new Err<DeploymentTargetConnectionRejected>(canonicalFailure);

        var clientResult = KubernetesClientSecurity.CreateForCandidate(
            canonical,
            cancellationToken);
        if (!clientResult.TryGetValue(out var client, out var clientFailure))
            return new Err<DeploymentTargetConnectionRejected>(clientFailure);

        byte[] serialized;
        try
        {
            serialized = KubeconfigCanonicalizer.Serialize(canonical);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            client.Dispose();
            throw new InvalidOperationException(
                "The deployment-target connection could not be serialized.",
                exception);
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
                return new Err<DeploymentTargetConnectionRejected>(encryptionFailure);
            }

            var persisted = DeploymentTargetConnectionFactory.CreateKubeconfig(
                connectionVersion,
                canonical.ContextName,
                canonical.ServerUrl,
                encrypted);
            return new Ok<PreparedDeploymentTargetConnection>(
                new PreparedDeploymentTargetConnection(persisted, client));
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            client.Dispose();
            throw new InvalidOperationException(
                "The deployment-target connection could not be protected.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
        }
    }

    private async ValueTask<Result<
        PreparedDeploymentTargetConnection,
        DeploymentTargetConnectionRejected>> PrepareServiceAccountAsync(
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
            return new Err<DeploymentTargetConnectionRejected>(canonicalFailure);

        var clientResult = KubernetesClientSecurity.CreateForCandidate(
            canonical,
            cancellationToken);
        if (!clientResult.TryGetValue(out var client, out var clientFailure))
            return new Err<DeploymentTargetConnectionRejected>(clientFailure);

        byte[] serialized;
        try
        {
            serialized = KubeconfigCanonicalizer.Serialize(canonical);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            client.Dispose();
            throw new InvalidOperationException(
                "The deployment-target connection could not be serialized.",
                exception);
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
                return new Err<DeploymentTargetConnectionRejected>(encryptionFailure);
            }

            var persisted = DeploymentTargetConnectionFactory.CreateServiceAccount(
                connectionVersion,
                canonical.ServerUrl,
                canonical.CertificateAuthorityData,
                encrypted);
            return new Ok<PreparedDeploymentTargetConnection>(
                new PreparedDeploymentTargetConnection(persisted, client));
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            client.Dispose();
            throw new InvalidOperationException(
                "The deployment-target connection could not be protected.",
                exception);
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
        {
            throw new ArgumentException(
                "The deployment-target connection owner is invalid.",
                nameof(deploymentTargetId));
        }
    }
}
