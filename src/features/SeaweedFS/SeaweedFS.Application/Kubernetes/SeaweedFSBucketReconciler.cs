using System.Text.Json;
using Pole.Features.Core.Kubernetes;
using Pole.Features.SeaweedFS.Domain;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.SeaweedFS.Application.Kubernetes;

internal sealed class SeaweedFSBucketReconciler(
    EnsureManagedSecretProjection ensureProjection,
    ReadManagedSecretKey readKey,
    SeaweedFSBucketOperations operations
)
{
    public Task<bool> ApplyAsync(
        SeaweedFSCustomObjects objects,
        SeaweedFSClusterManifestOptions options,
        SeaweedFSBucket bucket,
        CancellationToken ct) => bucket.Lifecycle is PurgingSeaweedFSLifecycle or DeletedSeaweedFSLifecycle
        ? Task.FromResult(true)
        : operations.RunAsync(
            bucket,
            current => current is null ? Task.FromResult(true) : ApplyCurrentAsync(objects, options, current, ct),
            ct);

    private async Task<bool> ApplyCurrentAsync(
        SeaweedFSCustomObjects objects,
        SeaweedFSClusterManifestOptions options,
        SeaweedFSBucket bucket,
        CancellationToken ct)
    {
        if (bucket.Lifecycle is PurgingSeaweedFSLifecycle or DeletedSeaweedFSLifecycle)
            return true;
        var bucketId = SeaweedFSNames.Bucket(bucket.Id);
        var policyId = SeaweedFSNames.BucketPolicy(bucket.Id);
        var retiring = bucket.Lifecycle is DeletingSeaweedFSLifecycle;
        var cluster = new { name = options.Name };

        object Manifest(
            string kind,
            string name,
            object spec) => new
        {
            apiVersion = SeaweedFSClusterManifests.SeaweedApiVersion,
            kind,
            metadata = new { name, @namespace = options.Namespace, labels = options.Identity.ToDictionary() },
            spec,
        };

        var identity = await objects.ApplyAsync(
            SeaweedFSObjectKind.Identity,
            Manifest("S3Identity", bucketId, new { seaweedRef = cluster, disabled = retiring }),
            ct);
        var replicated = options.Mode == SeaweedFSMode.Replicated;
        var resource = await objects.ApplyAsync(
            SeaweedFSObjectKind.Bucket,
            Manifest(
                "Bucket",
                bucketId,
                new
                {
                    seaweedRef = cluster, name = bucket.Name, reclaimPolicy = "Delete", adoptExisting = false,
                    placement = new
                    {
                        replication = replicated ? "001" : "000", fsync = true, volumeGrowthCount = replicated ? 2 : 1
                    },
                    access = new[] { new { user = bucketId, actions = new[] { "Read", "Write", "List" } } },
                }),
            ct);
        if (retiring)
        {
            var arn = $"arn:aws:s3:::{bucket.Name}";
            var deny = JsonSerializer.Serialize(
                new
                {
                    Version = "2012-10-17",
                    Statement = new[]
                    {
                        new
                        {
                            Effect = "Deny", Principal = "*", Action = "s3:*",
                            Resource = new[] { arn, $"{arn}/*" }
                        }
                    },
                });
            var policy = await objects.ApplyAsync(
                SeaweedFSObjectKind.Policy,
                Manifest("S3Policy", policyId, new { seaweedRef = cluster, policyDocument = deny }),
                ct);
            var binding = await objects.ApplyAsync(
                SeaweedFSObjectKind.Binding,
                Manifest(
                    "S3PolicyBinding",
                    policyId,
                    new
                    {
                        seaweedRef = cluster, policyRef = new { name = policyId },
                        subjects = new[] { new { kind = "Bucket", name = bucketId } }
                    }),
                ct);
            return SeaweedFSCustomObjects.Ready(binding) && SeaweedFSCustomObjects.Ready(policy) &&
                SeaweedFSCustomObjects.Ready(identity);
        }

        if (!await objects.DeleteAsync(SeaweedFSObjectKind.Binding, policyId, ct) ||
            !await objects.DeleteAsync(SeaweedFSObjectKind.Policy, policyId, ct))
            return false;
        var owner = Common.SeaweedFSCredentials.BucketOwner(bucket.SeaweedFSId, bucket.Id);
        var projection = await ensureProjection(
            new EnsureManagedSecretProjectionRequest(bucket.TenantId, owner, options.Identity.ResourceDeploymentId),
            ct);
        if (projection.Value is not SecretProjectionReference secret)
            return false;
        var credentials = await objects.ApplyAsync(
            SeaweedFSObjectKind.Credentials,
            Manifest(
                "S3Credentials",
                bucketId,
                new
                {
                    seaweedRef = cluster, identityRef = new { name = bucketId },
                    secretRef = new
                        { name = secret.KubernetesName, accessKeyField = "accessKey", secretKeyField = "secretKey" }
                }),
            ct);
        var expected = await readKey(new(bucket.TenantId, owner, "accessKey"), ct);
        return expected.Value is ManagedSecretKey key && (string?)credentials["status"]?["accessKey"] == key.Value &&
            SeaweedFSCustomObjects.Ready(resource) && SeaweedFSCustomObjects.Ready(identity) &&
            SeaweedFSCustomObjects.Ready(credentials);
    }

    public Task<bool> PurgeAsync(
        SeaweedFSCustomObjects objects,
        SeaweedFSBucket bucket,
        CancellationToken ct) => operations.RunAsync(
        bucket,
        current => current?.Lifecycle is PurgingSeaweedFSLifecycle or DeletedSeaweedFSLifecycle
            ? PurgeCurrentAsync(objects, current.Id, ct)
            : Task.FromResult(false),
        ct);

    private static async Task<bool> PurgeCurrentAsync(
        SeaweedFSCustomObjects objects,
        string id,
        CancellationToken ct)
    {
        var name = SeaweedFSNames.Bucket(id);
        var policy = SeaweedFSNames.BucketPolicy(id);
        // Keep the deny binding until the operator has confirmed data deletion via the Bucket finalizer.
        return await objects.DeleteAsync(SeaweedFSObjectKind.Bucket, name, ct) &&
            await objects.DeleteAsync(SeaweedFSObjectKind.Binding, policy, ct) &&
            await objects.DeleteAsync(SeaweedFSObjectKind.Policy, policy, ct) &&
            await objects.DeleteAsync(SeaweedFSObjectKind.Credentials, name, ct) &&
            await objects.DeleteAsync(SeaweedFSObjectKind.Identity, name, ct);
    }
}
