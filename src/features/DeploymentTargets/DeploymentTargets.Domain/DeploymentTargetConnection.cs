// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson.Serialization.Attributes;
using Pole.Encryption;

namespace Pole.Features.DeploymentTargets.Domain;

public enum DeploymentTargetConnectionKind
{
    InCluster,
    Kubeconfig,
    ServiceAccount,
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(InClusterDeploymentTargetConnection),
    typeof(KubeconfigDeploymentTargetConnection),
    typeof(ServiceAccountDeploymentTargetConnection))]
public abstract record DeploymentTargetConnection(
    long Version
)
{
    [BsonIgnore] public abstract DeploymentTargetConnectionKind Kind { get; }
}

[BsonDiscriminator(nameof(InClusterDeploymentTargetConnection))]
public sealed record InClusterDeploymentTargetConnection(
    long Version
) : DeploymentTargetConnection(Version)
{
    public override DeploymentTargetConnectionKind Kind => DeploymentTargetConnectionKind.InCluster;
}

[BsonDiscriminator(nameof(KubeconfigDeploymentTargetConnection))]
public sealed record KubeconfigDeploymentTargetConnection(
    long Version,
    string ContextName,
    string ServerUrl,
    EncryptedValue EncryptedKubeconfig
) : DeploymentTargetConnection(Version)
{
    public override DeploymentTargetConnectionKind Kind => DeploymentTargetConnectionKind.Kubeconfig;

    public override string ToString() =>
        $"KubeconfigDeploymentTargetConnection(Version={Version}, ContextName={ContextName}, ServerUrl={ServerUrl}, Credential=[REDACTED])";
}

[BsonDiscriminator(nameof(ServiceAccountDeploymentTargetConnection))]
public sealed record ServiceAccountDeploymentTargetConnection(
    long Version,
    string ServerUrl,
    string CertificateAuthorityData,
    EncryptedValue EncryptedCredential
) : DeploymentTargetConnection(Version)
{
    public override DeploymentTargetConnectionKind Kind => DeploymentTargetConnectionKind.ServiceAccount;

    public override string ToString() =>
        $"ServiceAccountDeploymentTargetConnection(Version={Version}, ServerUrl={ServerUrl}, CertificateAuthorityData=[REDACTED], Credential=[REDACTED])";
}

public static class DeploymentTargetConnectionFactory
{
    public static InClusterDeploymentTargetConnection CreateInCluster(long version) =>
        new(DeploymentTargetValueGuard.Positive(version, nameof(version)));

    public static KubeconfigDeploymentTargetConnection CreateKubeconfig(
        long version,
        string contextName,
        string serverUrl,
        EncryptedValue encryptedKubeconfig)
    {
        ArgumentNullException.ThrowIfNull(encryptedKubeconfig);

        return new KubeconfigDeploymentTargetConnection(
            DeploymentTargetValueGuard.Positive(version, nameof(version)),
            DeploymentTargetValueGuard.Required(
                contextName,
                nameof(contextName),
                DeploymentTargetValueGuard.MaximumContextNameLength),
            DeploymentTargetValueGuard.HttpsServerUrl(serverUrl, nameof(serverUrl)),
            encryptedKubeconfig);
    }

    public static ServiceAccountDeploymentTargetConnection CreateServiceAccount(
        long version,
        string serverUrl,
        string certificateAuthorityData,
        EncryptedValue encryptedCredential)
    {
        ArgumentNullException.ThrowIfNull(encryptedCredential);

        return new ServiceAccountDeploymentTargetConnection(
            DeploymentTargetValueGuard.Positive(version, nameof(version)),
            DeploymentTargetValueGuard.HttpsServerUrl(serverUrl, nameof(serverUrl)),
            DeploymentTargetValueGuard.Required(
                certificateAuthorityData,
                nameof(certificateAuthorityData),
                DeploymentTargetValueGuard.MaximumCertificateAuthorityDataLength),
            encryptedCredential);
    }
}
