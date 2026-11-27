// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Connections;

public sealed record StoredDeploymentTargetConnectionUnavailable;

/// <summary>
///     Recreates a Kubernetes client from a stored deployment-target connection.
///     The caller owns and must dispose a successful client.
/// </summary>
public delegate ValueTask<CreateStoredKubernetesClientOutcome> CreateDeploymentTargetKubernetesClient(
    string deploymentTargetId,
    DeploymentTargetConnection connection,
    CancellationToken cancellationToken = default);

internal sealed class DeploymentTargetKubernetesClientFactory(
    DeploymentTargetConnectionProtector protector
)
{
    public async ValueTask<CreateStoredKubernetesClientOutcome> InvokeAsync(
        string deploymentTargetId,
        DeploymentTargetConnection connection,
        CancellationToken cancellationToken = default)
    {
        EnsureValidOwner(deploymentTargetId, connection?.Version ?? 0);

        return connection switch
        {
            InClusterDeploymentTargetConnection => KubernetesClientSecurity.CreateInClusterForStored(cancellationToken),
            KubeconfigDeploymentTargetConnection kubeconfig => await CreateFromStoredKubeconfigAsync(
                deploymentTargetId,
                kubeconfig,
                cancellationToken),
            ServiceAccountDeploymentTargetConnection serviceAccount => await CreateFromStoredServiceAccountAsync(
                deploymentTargetId,
                serviceAccount,
                cancellationToken),
            _ => new StoredDeploymentTargetConnectionUnavailable(),
        };
    }

    private async ValueTask<CreateStoredKubernetesClientOutcome> CreateFromStoredKubeconfigAsync(
        string deploymentTargetId,
        KubeconfigDeploymentTargetConnection connection,
        CancellationToken cancellationToken)
    {
        var decryptedResult = await protector.UnprotectKubeconfigAsync(
            deploymentTargetId,
            connection.Version,
            connection.EncryptedKubeconfig,
            cancellationToken);
        if (decryptedResult is StoredDeploymentTargetConnectionUnavailable decryptionFailure)
            return decryptionFailure;
        var decrypted = (byte[])decryptedResult.Value;

        try
        {
            var canonicalResult = KubeconfigCanonicalizer.DeserializeStored(decrypted);
            if (canonicalResult is StoredDeploymentTargetConnectionUnavailable canonicalFailure)
                return canonicalFailure;
            var canonical = (CanonicalKubeconfigCredential)canonicalResult.Value;
            if (!string.Equals(canonical.ContextName, connection.ContextName, StringComparison.Ordinal) ||
                !string.Equals(canonical.ServerUrl, connection.ServerUrl, StringComparison.Ordinal))
                return new StoredDeploymentTargetConnectionUnavailable();

            return KubernetesClientSecurity.CreateForStored(canonical, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decrypted);
        }
    }

    private async ValueTask<CreateStoredKubernetesClientOutcome> CreateFromStoredServiceAccountAsync(
        string deploymentTargetId,
        ServiceAccountDeploymentTargetConnection connection,
        CancellationToken cancellationToken)
    {
        var decryptedResult = await protector.UnprotectServiceAccountCredentialAsync(
            deploymentTargetId,
            connection.Version,
            connection.EncryptedCredential,
            cancellationToken);
        if (decryptedResult is StoredDeploymentTargetConnectionUnavailable decryptionFailure)
            return decryptionFailure;
        var decrypted = (byte[])decryptedResult.Value;

        try
        {
            var canonicalResult = KubeconfigCanonicalizer.DeserializeStored(
                decrypted,
                ConnectionPayloadLimits.MaximumServiceAccountCredentialBytes);
            if (canonicalResult is StoredDeploymentTargetConnectionUnavailable)
                return new StoredDeploymentTargetConnectionUnavailable();
            var canonical = (CanonicalKubeconfigCredential)canonicalResult.Value;
            if (!KubeconfigCanonicalizer.IsServiceAccountCredential(canonical) ||
                !string.Equals(canonical.ServerUrl, connection.ServerUrl, StringComparison.Ordinal) || !string.Equals(
                    canonical.CertificateAuthorityData,
                    connection.CertificateAuthorityData,
                    StringComparison.Ordinal))
                return new StoredDeploymentTargetConnectionUnavailable();

            return KubernetesClientSecurity.CreateForStored(canonical, cancellationToken);
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
            throw new ArgumentException(
                "The stored deployment-target connection owner is invalid.",
                nameof(deploymentTargetId));
    }
}
