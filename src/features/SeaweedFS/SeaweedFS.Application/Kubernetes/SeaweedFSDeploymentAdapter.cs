using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using k8s;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Postgres.Contracts;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Lifecycle;
using Pole.Features.SeaweedFS.Application.Search;
using Pole.Features.SeaweedFS.Application.Services;
using Pole.Features.SeaweedFS.Domain;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.SeaweedFS.Application.Kubernetes;

internal sealed class SeaweedFSDeploymentAdapter(
    LoadSeaweedFSById load,
    LoadSeaweedFSBuckets loadBuckets,
    ReadManagedSecret readSecret,
    EnsurePostgresManagedDatabase ensureMetadata,
    IKubernetesClientProvider clients,
    SeaweedFSBucketReconciler bucketReconciler,
    SeaweedFSIngress ingress
) : IResourceDeploymentAdapter
{
    public async Task<ResourceDeploymentAdapterResult> ApplyAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken = default)
    {
        var deployment = context.Deployment;
        var ct = cancellationToken;
        var instance = await load(deployment.TenantId, deployment.ResourceId, ct);
        if (instance is null || deployment.Placement.Storage is null ||
            deployment.ResourceType != SeaweedFSResourceDeployment.ResourceType)
            return ResourceDeploymentAdapterResult.Failed.Create(
                "SeaweedFSPlacementMissing",
                "SeaweedFS placement is unavailable.",
                false);
        if (instance.Lifecycle is ActiveSeaweedFSLifecycle &&
            !string.IsNullOrWhiteSpace(deployment.Placement.Compute.RuntimeClassName))
            return ResourceDeploymentAdapterResult.Failed.Create(
                "RuntimeClassUnsupported",
                "This operator cannot apply a runtime class.",
                false);

        var options = Options(instance, deployment);
        var client = await clients.GetAsync(deployment.Placement.DeploymentTargetId, ct);
        var objects = new SeaweedFSCustomObjects(
            client,
            options.Namespace,
            options.Identity.ToDictionary(),
            deployment.DesiredGeneration);
        if (instance.Lifecycle is not ActiveSeaweedFSLifecycle)
        {
            // Closing must also work while the metadata provider or its credentials are unavailable.
            var current = await objects.ReadAsync(SeaweedFSObjectKind.Cluster, options.Name, ct);
            if (current?["spec"] is JsonObject spec && spec.Remove("s3"))
                await objects.ApplyAsync(
                    SeaweedFSObjectKind.Cluster,
                    new
                    {
                        apiVersion = SeaweedFSClusterManifests.SeaweedApiVersion, kind = "Seaweed",
                        metadata = new
                        {
                            name = options.Name, @namespace = options.Namespace,
                            labels = options.Identity.ToDictionary()
                        },
                        spec,
                    },
                    ct);
            var pods = await client.CoreV1.ListNamespacedPodAsync(
                options.Namespace,
                labelSelector: $"pole.io/resource-deployment-id={deployment.Id},app.kubernetes.io/component=s3",
                cancellationToken: ct);
            return pods.Items.Count == 0
                ? ResourceDeploymentAdapterResult.Succeeded.Create()
                : Pending("SeaweedFSAccessClosing", "Waiting for the private S3 gateway to stop.");
        }

        var storage = new SeaweedFSOwnedStorage(client, options);
        if (!await storage.SupportsAutomaticReclaimAsync(ct))
            return Pending(
                "SeaweedFSStorageReclaimUnsupported",
                "Select a storage class with Delete reclaim policy for automatic data cleanup.");
        if (!await ingress.ApplyAsync(
                deployment,
                instance.Spec.Metadata is DedicatedPostgresSeaweedFSMetadata,
                false,
                ct))
            return Pending(
                "SeaweedFSNetworkPolicyUnavailable",
                "Check the target operator network sources and isolation policy.");
        string? metadataPassword = null;
        if (instance.Spec.Metadata is ExistingPostgresSeaweedFSMetadata existing)
        {
            var result = await ensureMetadata(new(instance.TenantId, new("pole/seaweedfs", instance.Id)), ct);
            if (result.Value is not PostgresManagedDatabaseReady database ||
                database.Reference.PostgresClusterId != existing.PostgresId ||
                database.Runtime.DeploymentTargetId != deployment.Placement.DeploymentTargetId ||
                database.Runtime.Port != 5432 || database.Reference.Username != database.Credential.Username)
                return Pending(
                    "SeaweedFSMetadataPending",
                    "Waiting for the PostgreSQL database, role and network policy.");
            options = options with
            {
                ExistingMetadata = new(database.Runtime.Host, database.Reference.Database, database.Reference.Username)
            };
            metadataPassword = database.Credential.Password;
        }

        var manifests = SeaweedFSClusterManifests.Build(options);
        var adminResult = await readSecret(instance.TenantId, Common.SeaweedFSCredentials.AdminOwner(instance.Id), ct);
        if (adminResult.Value is not ManagedSecretValues admin)
            return Pending("SeaweedFSCredentialsUnavailable", "The administrator credential is unavailable.");
        await storage.EnsureSecretAsync(
            SeaweedFSClusterManifests.BootstrapSecret(
                options,
                new(admin.Values["accessKey"], admin.Values["secretKey"])),
            ct);
        if (manifests.MetadataCluster is not null)
        {
            var metadata = await storage.EnsureSecretAsync(
                SeaweedFSClusterManifests.MetadataCredentialsSecret(
                    options,
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(32))),
                ct);
            metadataPassword = Encoding.UTF8.GetString(metadata.Data["password"]);
            await objects.ApplyAsync(SeaweedFSObjectKind.Postgres, manifests.MetadataCluster, ct);
        }

        await storage.EnsureSecretAsync(
            SeaweedFSClusterManifests.FilerConfigurationSecret(options, metadataPassword),
            ct);
        var cluster = await objects.ApplyAsync(SeaweedFSObjectKind.Cluster, manifests.Seaweed, ct);
        if (manifests.RepairScript is not null)
            await objects.ApplyAsync(SeaweedFSObjectKind.Repair, manifests.RepairScript, ct);
        if (!SeaweedFSCustomObjects.Ready(cluster))
            return Pending("SeaweedFSNotReady", "Waiting for SeaweedFS components and metadata storage.");
        var buckets = await loadBuckets(instance.TenantId, instance.Id, ct);
        var allReady = true;
        foreach (var bucket in buckets)
            allReady &= await bucketReconciler.ApplyAsync(objects, options, bucket, ct);
        if (!allReady)
            return Pending("SeaweedFSBucketsPending", "Waiting for bucket and credential reconciliation.");
        return ResourceDeploymentAdapterResult.Succeeded.Create(
        [
            ResourceDeploymentEndpointDto.Create(
                "s3",
                $"{SeaweedFSNames.S3Service(instance.Id)}.{options.Namespace}.svc.cluster.local",
                8333,
                "http")
        ]);
    }

    public async Task<ResourceDeploymentAdapterResult> DeleteAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var deployment = context.Deployment;
        var instance = await load(deployment.TenantId, deployment.ResourceId, ct);
        if (instance?.Lifecycle is not PurgingSeaweedFSLifecycle || deployment.Placement.Storage is null)
            return Pending("SeaweedFSPurgeNotAuthorized", "Waiting for the resource deletion deadline.");
        var options = Options(instance, deployment);
        var client = await clients.GetAsync(deployment.Placement.DeploymentTargetId, ct);
        var objects = new SeaweedFSCustomObjects(
            client,
            options.Namespace,
            options.Identity.ToDictionary(),
            deployment.DesiredGeneration);
        var ready = true;
        foreach (var bucket in await loadBuckets(instance.TenantId, instance.Id, ct))
            ready &= await bucketReconciler.PurgeAsync(objects, bucket, ct);
        if (!ready)
            return Pending("SeaweedFSBucketCleanup", "Waiting for bucket finalizers.");
        if (options.Mode == SeaweedFSMode.Replicated && !await objects.DeleteAsync(
                SeaweedFSObjectKind.Repair,
                $"{options.Name}-repair",
                ct))
            return Pending("SeaweedFSRepairCleanup", "Waiting for repair workloads to terminate.");
        if (!await objects.DeleteAsync(SeaweedFSObjectKind.Cluster, options.Name, ct))
            return Pending("SeaweedFSCleanup", "Waiting for SeaweedFS workloads to terminate.");
        if (instance.Spec.Metadata is DedicatedPostgresSeaweedFSMetadata && !await objects.DeleteAsync(
                SeaweedFSObjectKind.Postgres,
                $"{options.Name}-metadata",
                ct))
            return Pending("SeaweedFSMetadataCleanup", "Waiting for the metadata cluster to terminate.");
        var storage = new SeaweedFSOwnedStorage(client, options);
        if (!await storage.CleanupAsync(ct))
            return Pending("SeaweedFSStorageCleanup", "Waiting for storage cleanup.");
        if (!await ingress.ApplyAsync(
                deployment,
                instance.Spec.Metadata is DedicatedPostgresSeaweedFSMetadata,
                true,
                ct))
            return Pending("SeaweedFSNetworkCleanup", "Waiting for network policy cleanup.");
        return ResourceDeploymentAdapterResult.Succeeded.Create();
    }

    private static ResourceDeploymentAdapterResult Pending(string code, string message) =>
        ResourceDeploymentAdapterResult.Failed.Create(code, message, retryable: true);

    internal static SeaweedFSClusterManifestOptions Options(
        SeaweedFSService instance,
        ResourceDeploymentReconciliationSnapshot deployment)
    {
        var name = SeaweedFSNames.Instance(instance.Id);
        var placement = deployment.Placement;
        var labels = new WorkloadIdentityLabels(
            deployment.TenantId,
            deployment.ResourceGroupId,
            deployment.IsolationScopeId,
            deployment.RuntimeCellId,
            placement.DeploymentTargetId,
            SeaweedFSResourceDeployment.KubernetesResourceTypeLabel,
            deployment.ResourceId,
            deployment.Id);
        return new(
            name,
            placement.KubernetesNamespace,
            instance.Spec.Mode,
            instance.Spec.Storage.SizeGb,
            placement.Storage!,
            placement.Compute,
            labels,
            $"{name}-admin-config",
            $"{name}-filer-config",
            $"{name}-metadata-auth");
    }
}

public static class SeaweedFSRuntimeRegistration
{
    public static IServiceCollection AddSeaweedFSRuntime(this IServiceCollection services)
    {
        services.AddScoped<SeaweedFSDeploymentAdapter>();
        services.AddScoped<SeaweedFSIngress>();
        services.AddScoped<SeaweedFSDeploymentObserver>();
        services.AddSingleton(
            ResourceDeploymentObserverDescriptor.Create<SeaweedFSDeploymentObserver>(
                SeaweedFSResourceDeployment.ResourceType));
        // API slice hosts do not compose Hangfire storage; resolve it only for native work.
        services.AddScoped(provider => new SeaweedFSBucketOperations(
            provider.GetRequiredService<Hangfire.JobStorage>(),
            provider.GetRequiredService<LoadSeaweedFSBucket>()));
        services.AddScoped<SeaweedFSBucketReconciler>();
        services.AddScoped<SeaweedFSBucketDataPurger>();
        services.AddScoped<PurgeSeaweedFSBucketData>(provider =>
            provider.GetRequiredService<SeaweedFSBucketDataPurger>().InvokeAsync);
        services.AddSingleton(
            ResourceDeploymentAdapterDescriptor.Create<SeaweedFSDeploymentAdapter>(
                SeaweedFSResourceDeployment.ResourceType,
                ResourcePlacementCapability.SingleTarget));
        foreach (var definition in SeaweedFSIngress.Definitions)
            services.AddSingleton(definition);
        services.AddSingleton(SeaweedFSSearchProjection.SourceType);
        return services;
    }
}
