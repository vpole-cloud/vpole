using MongoDB.Bson.Serialization.Attributes;
using Pole.Encryption;

namespace Pole.Features.DeploymentTargets.Domain;

public enum DeploymentTargetConnectionKind
{
    InCluster,
    Kubeconfig,
    ServiceAccount
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(InClusterDeploymentTargetConnection),
    typeof(KubeconfigDeploymentTargetConnection),
    typeof(ServiceAccountDeploymentTargetConnection))]
public abstract record DeploymentTargetConnection
{
    protected DeploymentTargetConnection(long version)
    {
        Version = DeploymentTargetValueGuard.Positive(version, nameof(version));
    }

    public long Version { get; }

    [BsonIgnore]
    public abstract DeploymentTargetConnectionKind Kind { get; }
}

[BsonDiscriminator(nameof(InClusterDeploymentTargetConnection))]
public sealed record InClusterDeploymentTargetConnection : DeploymentTargetConnection
{
    [BsonConstructor]
    public InClusterDeploymentTargetConnection(long version)
        : base(version)
    {
    }

    public override DeploymentTargetConnectionKind Kind => DeploymentTargetConnectionKind.InCluster;
}

[BsonDiscriminator(nameof(KubeconfigDeploymentTargetConnection))]
public sealed record KubeconfigDeploymentTargetConnection : DeploymentTargetConnection
{
    [BsonConstructor]
    public KubeconfigDeploymentTargetConnection(
        long version,
        string contextName,
        string serverUrl,
        EncryptedValue encryptedKubeconfig)
        : base(version)
    {
        ContextName = DeploymentTargetValueGuard.Required(
            contextName,
            nameof(contextName),
            DeploymentTargetValueGuard.MaximumContextNameLength);
        ServerUrl = DeploymentTargetValueGuard.HttpsServerUrl(serverUrl, nameof(serverUrl));
        EncryptedKubeconfig = encryptedKubeconfig ??
            throw new ArgumentNullException(nameof(encryptedKubeconfig));
    }

    public string ContextName { get; }
    public string ServerUrl { get; }
    public EncryptedValue EncryptedKubeconfig { get; }

    public override DeploymentTargetConnectionKind Kind => DeploymentTargetConnectionKind.Kubeconfig;

    public override string ToString() =>
        $"KubeconfigDeploymentTargetConnection(Version={Version}, ContextName={ContextName}, ServerUrl={ServerUrl}, Credential=[REDACTED])";
}

[BsonDiscriminator(nameof(ServiceAccountDeploymentTargetConnection))]
public sealed record ServiceAccountDeploymentTargetConnection : DeploymentTargetConnection
{
    [BsonConstructor]
    public ServiceAccountDeploymentTargetConnection(
        long version,
        string serverUrl,
        string certificateAuthorityData,
        EncryptedValue encryptedCredential)
        : base(version)
    {
        ServerUrl = DeploymentTargetValueGuard.HttpsServerUrl(serverUrl, nameof(serverUrl));
        CertificateAuthorityData = DeploymentTargetValueGuard.Required(
            certificateAuthorityData,
            nameof(certificateAuthorityData),
            DeploymentTargetValueGuard.MaximumCertificateAuthorityDataLength);
        EncryptedCredential = encryptedCredential ??
            throw new ArgumentNullException(nameof(encryptedCredential));
    }

    public string ServerUrl { get; }
    public string CertificateAuthorityData { get; }
    public EncryptedValue EncryptedCredential { get; }

    public override DeploymentTargetConnectionKind Kind => DeploymentTargetConnectionKind.ServiceAccount;

    public override string ToString() =>
        $"ServiceAccountDeploymentTargetConnection(Version={Version}, ServerUrl={ServerUrl}, CertificateAuthorityData=[REDACTED], Credential=[REDACTED])";
}
