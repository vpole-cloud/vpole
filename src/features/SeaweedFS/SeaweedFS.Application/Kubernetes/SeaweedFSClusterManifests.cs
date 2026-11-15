using System.Text;
using System.Text.Json;
using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Domain;

namespace Pole.Features.SeaweedFS.Application.Kubernetes;

public sealed record SeaweedFSClusterManifestOptions(
    string Name,
    string Namespace,
    SeaweedFSMode Mode,
    int VolumeSizeGb,
    ResourceDeploymentStorageConfiguration Storage,
    ResourceDeploymentComputeConfiguration Compute,
    WorkloadIdentityLabels Identity,
    string BootstrapS3SecretName,
    string FilerConfigurationSecretName,
    string MetadataCredentialsSecretName,
    SeaweedFSMetadataConnection? ExistingMetadata = null
);

public sealed record SeaweedFSMetadataConnection(
    string Host,
    string Database,
    string Username
);

public sealed record SeaweedFSClusterManifestBundle(
    IDictionary<string, object> Seaweed,
    IDictionary<string, object>? MetadataCluster,
    IDictionary<string, object>? RepairScript
);

/// <summary>Credentials remain confined to create-only owned Secrets, never CRs or record diagnostics.</summary>
public sealed class SeaweedFSCredentials(
    string accessKey,
    string secretKey
)
{
    public string AccessKey { get; } = Required(accessKey);
    public string SecretKey { get; } = Required(secretKey);
    public override string ToString() => "[REDACTED]";

    private static string Required(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}

/// <summary>Release-specific SeaweedFS operator 0.1.47 / 1.0.44 manifests.</summary>
public static class SeaweedFSClusterManifests
{
    public const string SeaweedApiVersion = "seaweed.seaweedfs.com/v1";
    public const string SeaweedImage = "docker.io/chrislusf/seaweedfs:4.53";
    public const string MetadataImage = "ghcr.io/cloudnative-pg/postgresql:17.6-standard-trixie";
    public const string BootstrapConfigurationKey = "s3.json";
    public const string FilerConfigurationKey = "filer.toml";
    public const string MetadataDatabase = "seaweedfs";
    public const string MetadataUsername = "seaweedfs";
    public const int LogicalVolumeSizeLimitMb = 64;
    public const int VolumeReservedStorageMb = 64;

    public static SeaweedFSClusterManifestBundle Build(SeaweedFSClusterManifestOptions options)
    {
        Validate(options);
        var topology = SeaweedFSTopology.For(options.Mode);
        var replicated = options.Mode == SeaweedFSMode.Replicated;
        var master = Component(options, "master", topology.Masters);
        master["defaultReplication"] = replicated ? "001" : "000";
        master["persistence"] = Persistence(options, SeaweedFSTopology.MasterDiskGb);
        // Keep allocation granular and leave per-node filesystem/index overhead outside volume capacity.
        master["volumeSizeLimitMB"] = LogicalVolumeSizeLimitMb;
        master["config"] = "[master.volume_growth]\ncopy_1 = 1\ncopy_2 = 1\ncopy_3 = 1\ncopy_other = 1\n";
        var volume = Component(options, "volume", topology.Volumes);
        ((Dictionary<string, string>)volume["requests"])["storage"] = $"{options.VolumeSizeGb}Gi";
        volume["storageClassName"] = options.Storage.StorageClassName;
        volume["maxVolumeCounts"]
            = checked(options.VolumeSizeGb * 1024 - VolumeReservedStorageMb) / LogicalVolumeSizeLimitMb;
        volume["minFreeSpacePercent"] = 1;
        volume["storageLabels"] = options.Identity.ToDictionary();
        var filer = Component(options, "filer", topology.Filers);
        filer["configSecret"] = new { name = options.FilerConfigurationSecretName, key = FilerConfigurationKey };
        if (!replicated)
            filer["persistence"] = Persistence(options, SeaweedFSTopology.SingleFilerDiskGb);
        var gateway = Component(options, "s3", topology.Gateways);
        gateway["configSecret"] = new { name = options.BootstrapS3SecretName, key = BootstrapConfigurationKey };
        // The user-facing IAM API shares the S3 endpoint; bootstrap Admin stays out of band.
        gateway["iam"] = true;
        var spec = new Dictionary<string, object>
        {
            ["image"] = SeaweedImage,
            ["volumeServerDiskCount"] = 1,
            ["labels"] = options.Identity.ToDictionary(),
            ["master"] = master,
            ["volume"] = volume,
            ["filer"] = filer,
            ["s3"] = gateway,
        };
        return new SeaweedFSClusterManifestBundle(
            CustomResource(SeaweedApiVersion, "Seaweed", options.Name, options, spec),
            replicated && options.ExistingMetadata is null ? BuildMetadataCluster(options) : null,
            replicated ? BuildRepairScript(options) : null);
    }

    public static V1Secret BootstrapSecret(SeaweedFSClusterManifestOptions options, SeaweedFSCredentials credentials)
    {
        Validate(options);
        ArgumentNullException.ThrowIfNull(credentials);
        var json = JsonSerializer.Serialize(
            new
            {
                identities = new[]
                {
                    new
                    {
                        name = "pole-bootstrap-admin",
                        credentials = new[]
                            { new { accessKey = credentials.AccessKey, secretKey = credentials.SecretKey } },
                        actions = new[] { "Admin" },
                    }
                },
            });
        return Secret(options, options.BootstrapS3SecretName, new() { [BootstrapConfigurationKey] = json });
    }

    public static V1Secret MetadataCredentialsSecret(SeaweedFSClusterManifestOptions options, string password)
    {
        Validate(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (options.Mode != SeaweedFSMode.Replicated || options.ExistingMetadata is not null)
            throw new ArgumentException(
                "Only dedicated replicated metadata creates PostgreSQL credentials.",
                nameof(options));
        var result = Secret(
            options,
            options.MetadataCredentialsSecretName,
            new() { ["username"] = MetadataUsername, ["password"] = password });
        result.Type = "kubernetes.io/basic-auth";
        return result;
    }

    public static V1Secret FilerConfigurationSecret(SeaweedFSClusterManifestOptions options, string? metadataPassword)
    {
        Validate(options);
        string configuration;
        if (options.Mode == SeaweedFSMode.SingleNode)
        {
            configuration = "[leveldb2]\nenabled = true\ndir = \"/data/filerldb2\"\n";
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(metadataPassword);
            // JSON basic strings are a safe TOML basic-string subset for generated credentials.
            var password = JsonSerializer.Serialize(metadataPassword);
            var metadata = options.ExistingMetadata ?? new SeaweedFSMetadataConnection(
                $"{options.Name}-metadata-rw",
                MetadataDatabase,
                MetadataUsername);
            var host = JsonSerializer.Serialize(metadata.Host);
            var username = JsonSerializer.Serialize(metadata.Username);
            var database = JsonSerializer.Serialize(metadata.Database);
            configuration = $"""
                             [postgres]
                             enabled = true
                             createTable = true
                             hostname = {host}
                             port = 5432
                             username = {username}
                             password = {password}
                             database = {database}
                             sslmode = "require"
                             connection_max_idle = 4
                             connection_max_open = 0
                             connection_max_lifetime_seconds = 60
                             enableUpsert = true
                             """;
        }

        return Secret(options, options.FilerConfigurationSecretName, new() { [FilerConfigurationKey] = configuration });
    }

    private static V1Secret Secret(
        SeaweedFSClusterManifestOptions options,
        string name,
        Dictionary<string, string> values) => new()
    {
        ApiVersion = "v1",
        Kind = "Secret",
        Metadata = new V1ObjectMeta
            { Name = name, NamespaceProperty = options.Namespace, Labels = options.Identity.ToDictionary() },
        Type = "Opaque",
        Data = values.ToDictionary(pair => pair.Key, pair => Encoding.UTF8.GetBytes(pair.Value)),
    };

    private static Dictionary<string, object> Component(
        SeaweedFSClusterManifestOptions options,
        string component,
        int replicas)
    {
        var compute = options.Compute.ToKubernetesCompute();
        var resources = compute.ToResourceRequirements();
        var result = new Dictionary<string, object>
        {
            ["replicas"] = replicas,
            ["labels"] = options.Identity.ToDictionary(),
            ["requests"] = ResourceValues(resources.Requests),
            ["limits"] = ResourceValues(resources.Limits),
        };
        var nodeSelector = compute.ToNodeSelector();
        if (nodeSelector is not null)
            result["nodeSelector"] = nodeSelector;
        var tolerations = compute.ToTolerations();
        if (tolerations is not null)
            result["tolerations"] = tolerations;
        var affinity = new V1Affinity { NodeAffinity = compute.ToNodeAffinity() };
        if (options.Mode == SeaweedFSMode.Replicated)
        {
            var labels = options.Identity.ToDictionary();
            labels["app.kubernetes.io/component"] = component;
            affinity.PodAntiAffinity = new V1PodAntiAffinity
            {
                RequiredDuringSchedulingIgnoredDuringExecution =
                [
                    new V1PodAffinityTerm
                    {
                        TopologyKey = "kubernetes.io/hostname",
                        LabelSelector = new V1LabelSelector { MatchLabels = labels },
                    }
                ],
            };
        }

        if (affinity.NodeAffinity is not null || affinity.PodAntiAffinity is not null)
            result["affinity"] = affinity;
        return result;
    }

    private static Dictionary<string, string> ResourceValues(IDictionary<string, ResourceQuantity> values) =>
        values.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);

    private static object Persistence(SeaweedFSClusterManifestOptions options, int sizeGb) => new
    {
        enabled = true,
        storageClassName = options.Storage.StorageClassName,
        resources = new { requests = new Dictionary<string, string> { ["storage"] = $"{sizeGb}Gi" } },
        labels = options.Identity.ToDictionary(),
        accessModes = new[] { "ReadWriteOnce" },
    };

    private static IDictionary<string, object> CustomResource(
        string apiVersion,
        string kind,
        string name,
        SeaweedFSClusterManifestOptions options,
        object spec) => new Dictionary<string, object>
    {
        ["apiVersion"] = apiVersion,
        ["kind"] = kind,
        ["metadata"] = new { name, @namespace = options.Namespace, labels = options.Identity.ToDictionary() },
        ["spec"] = spec,
    };

    private static void Validate(SeaweedFSClusterManifestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BootstrapS3SecretName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FilerConfigurationSecretName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.MetadataCredentialsSecretName);
        ArgumentNullException.ThrowIfNull(options.Compute);
        ArgumentNullException.ThrowIfNull(options.Storage);
        ArgumentNullException.ThrowIfNull(options.Identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Storage.StorageClassName);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.VolumeSizeGb, 1);
        // ComponentSpec in the pinned operator has no runtimeClassName field.
        if (!string.IsNullOrWhiteSpace(options.Compute.RuntimeClassName))
            throw new ArgumentException(
                "SeaweedFS operator 0.1.47 does not support RuntimeClassName.",
                nameof(options));
        if (options.ExistingMetadata is not null)
        {
            if (options.Mode != SeaweedFSMode.Replicated)
                throw new ArgumentException(
                    "Existing PostgreSQL metadata is only supported in replicated mode.",
                    nameof(options));
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ExistingMetadata.Host);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ExistingMetadata.Database);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ExistingMetadata.Username);
        }

        if (options.Mode is not (SeaweedFSMode.SingleNode or SeaweedFSMode.Replicated))
            throw new ArgumentOutOfRangeException(nameof(options), "Unsupported SeaweedFS mode.");
    }

    private static IDictionary<string, object> BuildMetadataCluster(SeaweedFSClusterManifestOptions options)
    {
        var compute = options.Compute.ToKubernetesCompute();
        var resources = compute.ToResourceRequirements();
        var affinity = new Dictionary<string, object>
        {
            ["enablePodAntiAffinity"] = true,
            ["podAntiAffinityType"] = "required",
            ["topologyKey"] = "kubernetes.io/hostname",
        };
        var nodeSelector = compute.ToNodeSelector();
        if (nodeSelector is not null)
            affinity["nodeSelector"] = nodeSelector;
        var nodeAffinity = compute.ToNodeAffinity();
        if (nodeAffinity is not null)
            affinity["nodeAffinity"] = nodeAffinity;
        var tolerations = compute.ToTolerations();
        if (tolerations is not null)
            affinity["tolerations"] = tolerations;
        var spec = new Dictionary<string, object>
        {
            ["instances"] = SeaweedFSTopology.For(options.Mode).MetadataInstances,
            ["imageName"] = MetadataImage,
            ["enableSuperuserAccess"] = false,
            ["inheritedMetadata"] = new { labels = options.Identity.ToDictionary() },
            ["bootstrap"] = new
            {
                initdb = new
                {
                    database = MetadataDatabase,
                    owner = MetadataUsername,
                    secret = new { name = options.MetadataCredentialsSecretName },
                    // Locale-independent byte ordering matches S3 key ordering.
                    encoding = "UTF8",
                    localeCollate = "C",
                    localeCType = "C",
                }
            },
            ["storage"] = new
                { size = $"{SeaweedFSTopology.MetadataDiskGb}Gi", storageClass = options.Storage.StorageClassName },
            ["resources"] = new
                { requests = ResourceValues(resources.Requests), limits = ResourceValues(resources.Limits) },
            ["affinity"] = affinity,
            ["postgresql"] = new
            {
                synchronous = new { method = "any", number = 1, dataDurability = "required", failoverQuorum = true },
            },
        };
        return CustomResource("postgresql.cnpg.io/v1", "Cluster", $"{options.Name}-metadata", options, spec);
    }

    private static IDictionary<string, object> BuildRepairScript(SeaweedFSClusterManifestOptions options)
    {
        var compute = options.Compute.ToKubernetesCompute();
        var spec = new Dictionary<string, object>
        {
            ["clusterRef"] = new { name = options.Name },
            ["schedule"] = "*/1 * * * *",
            ["script"] = "lock\nvolume.fix.replication -apply\nunlock\n",
            ["clusterPodLabels"] = options.Identity.ToDictionary(),
            ["concurrencyPolicy"] = "Forbid",
            ["activeDeadlineSeconds"] = 300,
            ["backoffLimit"] = 0,
            ["successfulJobsHistoryLimit"] = 1,
            ["failedJobsHistoryLimit"] = 1,
            ["resources"] = compute.ToResourceRequirements(),
        };
        var nodeSelector = compute.ToNodeSelector();
        if (nodeSelector is not null)
            spec["nodeSelector"] = nodeSelector;
        var nodeAffinity = compute.ToNodeAffinity();
        if (nodeAffinity is not null)
            spec["affinity"] = new k8s.Models.V1Affinity { NodeAffinity = nodeAffinity };
        var tolerations = compute.ToTolerations();
        if (tolerations is not null)
            spec["tolerations"] = tolerations;
        return CustomResource(SeaweedApiVersion, "AdminScript", $"{options.Name}-repair", options, spec);
    }
}
