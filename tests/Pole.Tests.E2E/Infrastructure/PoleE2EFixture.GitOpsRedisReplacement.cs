// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

public sealed record GitOpsNativeIdentity(
    string Name,
    string Uid
);

public sealed record GitOpsRedisNativeSnapshot(
    GitOpsNativeIdentity Namespace,
    GitOpsNativeIdentity StatefulSet,
    GitOpsNativeIdentity Pod,
    GitOpsNativeIdentity PersistentVolumeClaim
);

public sealed partial class PoleE2EFixture
{
    public async Task<GitOpsRedisNativeSnapshot> WaitForGitOpsRedisNativeAsync(string redisId, CancellationToken ct)
    {
        var placement = await GitOpsPlacementAsync(redisId, ct);
        var ns = await GitOpsNamespaceAsync(placement, ct);
        var selector = $"pole.io/resource-id={redisId}," +
            $"pole.io/resource-deployment-id={placement["_id"].AsObjectId}";
        var namespaceLabels = new Dictionary<string, string>
        {
            ["pole.io/tenant-id"] = Alpha.DefaultTenantId,
            ["pole.io/resource-group-id"] = placement["ResourceGroupId"].AsObjectId.ToString(),
            ["pole.io/isolation-scope-id"] = placement["IsolationScopeId"].AsObjectId.ToString(),
            ["pole.io/runtime-cell-id"] = placement["RuntimeCellId"].AsObjectId.ToString(),
            ["pole.io/deployment-target-id"] = placement["ResolvedPlacement"]["DeploymentTargetId"]
                .AsObjectId
                .ToString(),
        };
        var workloadLabels = new Dictionary<string, string>(namespaceLabels)
        {
            ["pole.io/resource-type"] = "Redis",
            ["pole.io/resource-id"] = redisId,
            ["pole.io/resource-deployment-id"] = placement["_id"].AsObjectId.ToString(),
        };
        GitOpsRedisNativeSnapshot? observed = null;
        await TestWait.UntilAsync(
            async token =>
            {
                var nativeNamespace = await ReadNamespaceIfPresentAsync(ns, token);
                if (nativeNamespace is null)
                    return false;

                VerifyOwnedNamespace(nativeNamespace);
                AssertNativeLabels(nativeNamespace.Metadata, namespaceLabels);
                var controllers = await _kubernetes.AppsV1.ListNamespacedStatefulSetAsync(
                    ns,
                    labelSelector: selector,
                    cancellationToken: token);
                var controller = controllers.Items.SingleOrDefault();
                if (controller is null || controller.Metadata.DeletionTimestamp is not null || controller.Status is not
                    {
                        ObservedGeneration: { } generation, ReadyReplicas: 1,
                        UpdatedReplicas: 1,
                    } status || generation < controller.Metadata.Generation ||
                    status.CurrentRevision != status.UpdateRevision)
                    return false;

                var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync(
                    ns,
                    labelSelector: selector,
                    cancellationToken: token);
                var pod = pods.Items.SingleOrDefault(item => item.Metadata.OwnerReferences?.Any(owner =>
                        owner.Kind == "StatefulSet" && owner.Uid == controller.Metadata.Uid &&
                        owner.Controller == true) ==
                    true);
                if (pod?.Metadata.DeletionTimestamp is not null || pod?.Status?.Phase != "Running" ||
                    pod.Status.Conditions?.Any(condition => condition.Type == "Ready" && condition.Status == "True") !=
                    true)
                    return false;

                var claimName = Assert.Single(pod.Spec.Volumes, volume => volume.PersistentVolumeClaim is not null)
                    .PersistentVolumeClaim.ClaimName;
                var claim = await _kubernetes.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(
                    claimName,
                    ns,
                    cancellationToken: token);
                if (claim.Metadata.DeletionTimestamp is not null || claim.Status?.Phase != "Bound")
                    return false;

                AssertNativeLabels(controller.Metadata, workloadLabels);
                AssertNativeLabels(pod.Metadata, workloadLabels);
                AssertNativeLabels(claim.Metadata, workloadLabels);
                Assert.Equal("Delete", controller.Spec.PersistentVolumeClaimRetentionPolicy.WhenDeleted);
                Assert.Equal("Retain", controller.Spec.PersistentVolumeClaimRetentionPolicy.WhenScaled);
                observed = new GitOpsRedisNativeSnapshot(
                    NativeIdentity(nativeNamespace.Metadata),
                    NativeIdentity(controller.Metadata),
                    NativeIdentity(pod.Metadata),
                    NativeIdentity(claim.Metadata));
                return true;
            },
            TimeSpan.FromMinutes(3),
            "The accepted Redis identity did not become a current ready native workload.",
            ct,
            TimeSpan.FromSeconds(2));
        return observed!;
    }

    public async Task AssertGitOpsRedisNativeReplacedAsync(
        GitOpsRedisNativeSnapshot before,
        GitOpsRedisNativeSnapshot after,
        CancellationToken ct)
    {
        Assert.Equal(before.Namespace, after.Namespace);
        Assert.NotEqual(before.StatefulSet.Uid, after.StatefulSet.Uid);
        Assert.NotEqual(before.Pod.Uid, after.Pod.Uid);
        Assert.NotEqual(before.PersistentVolumeClaim.Uid, after.PersistentVolumeClaim.Uid);
        VerifyOwnedNamespace(
            await _kubernetes.CoreV1.ReadNamespaceAsync(before.Namespace.Name, cancellationToken: ct),
            before.Namespace.Uid);
        await TestWait.UntilAsync(
            async token =>
            {
                var controllers = await _kubernetes.AppsV1.ListNamespacedStatefulSetAsync(
                    before.Namespace.Name,
                    cancellationToken: token);
                var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync(
                    before.Namespace.Name,
                    cancellationToken: token);
                var claims = await _kubernetes.CoreV1.ListNamespacedPersistentVolumeClaimAsync(
                    before.Namespace.Name,
                    cancellationToken: token);
                return controllers.Items.All(item => item.Metadata.Uid != before.StatefulSet.Uid) &&
                    pods.Items.All(item => item.Metadata.Uid != before.Pod.Uid) &&
                    claims.Items.All(item => item.Metadata.Uid != before.PersistentVolumeClaim.Uid);
            },
            TimeSpan.FromMinutes(2),
            "The replaced Redis native identities still exist.",
            ct);

        // The owner requests PVC deletion. The StorageClass reclaim policy decides what happens to backing data.
        // New PVC identity proves replacement; it does not establish secure erasure or data migration.
    }

    public async Task AssertGitOpsRedisReplacementRecordedAsync(
        string source,
        string run,
        string oldId,
        string newId,
        CancellationToken ct)
    {
        var database = await GitOpsDatabaseAsync(ct);
        var inventory = await database.GetCollection<BsonDocument>("ManagedDeclarations")
            .Find(
                new BsonDocument
                {
                    { "TenantId", Alpha.DefaultTenantId }, { "SourceId", source }, { "Kind", "RedisService" },
                })
            .SingleAsync(ct);
        Assert.Equal(newId, inventory["ResourceId"].AsString);
        Assert.Equal(run, inventory["LastRunId"].AsString);
        var resources = database.GetCollection<BsonDocument>("RedisServices");
        Assert.Equal(
            0,
            await resources.CountDocumentsAsync(
                new BsonDocument
                {
                    { "_id", ObjectId.Parse(oldId) }, { "TenantId", Alpha.DefaultTenantId },
                },
                cancellationToken: ct));
        Assert.Equal(
            1,
            await resources.CountDocumentsAsync(
                new BsonDocument
                {
                    { "_id", ObjectId.Parse(newId) }, { "TenantId", Alpha.DefaultTenantId },
                },
                cancellationToken: ct));
        var receipts = await database.GetCollection<BsonDocument>("GitOperationReceipts")
            .Find(
                new BsonDocument
                {
                    { "TenantId", Alpha.DefaultTenantId }, { "SourceId", source }, { "RunId", run },
                    { "Kind", "RedisService" },
                })
            .ToListAsync(ct);
        Assert.All(receipts, receipt => Assert.True(receipt["Blocker"].IsBsonNull));
        Assert.Equal(
            receipts.Count,
            receipts.Select(receipt => (receipt["ResourceId"].AsString, receipt["Phase"].AsString)).Distinct().Count());
        Assert.Single(
            receipts,
            receipt => receipt["ResourceId"] == oldId &&
                receipt["Phase"].AsString.EndsWith("/BeginRemoval", StringComparison.Ordinal));
        Assert.Single(
            receipts,
            receipt => receipt["ResourceId"] == oldId &&
                receipt["Phase"].AsString.EndsWith("/RemovalCompleted", StringComparison.Ordinal));
        Assert.Single(receipts, receipt => receipt["ResourceId"] == newId && receipt["Phase"] == "Prepare");
        Assert.Single(receipts, receipt => receipt["ResourceId"] == newId && receipt["Phase"] == "Configure");
    }

    public async Task<long> CountGitOpsRedisScenarioBuildJobsAsync(CancellationToken ct)
    {
        var database = await GitOpsDatabaseAsync(ct);
        return await database.GetCollection<BsonDocument>("SourceBuildJobs")
            .CountDocumentsAsync(new BsonDocument("TenantId", Alpha.DefaultTenantId), cancellationToken: ct);
    }

    public async Task<string> ReadGitOpsRedisReceiptMetadataAsync(
        string source,
        string run,
        CancellationToken ct)
    {
        var database = await GitOpsDatabaseAsync(ct);
        var receipts = await database.GetCollection<BsonDocument>("GitOperationReceipts")
            .Find(
                new BsonDocument
                {
                    { "TenantId", Alpha.DefaultTenantId }, { "SourceId", source }, { "RunId", run },
                })
            .Project(
                new BsonDocument
                {
                    { "Phase", 1 }, { "PlanRevision", 1 }, { "Lifecycle.Postcondition.Versions", 1 },
                    { "Lifecycle.RelatedVersions", 1 },
                })
            .Limit(30)
            .ToListAsync(ct);
        return string.Join(
            "; ",
            receipts.OrderBy(item => item["Phase"].AsString, StringComparer.Ordinal)
                .Select(receipt =>
                {
                    var lifecycle = receipt.GetValue("Lifecycle", BsonNull.Value);
                    if (lifecycle is not BsonDocument effect)
                        return receipt["Phase"].AsString + "@plan" + receipt["PlanRevision"].ToInt64();
                    var postcondition = effect.GetValue("Postcondition", BsonNull.Value) as BsonDocument;
                    return receipt["Phase"].AsString + ":post=" +
                        VersionMetadata(postcondition?.GetValue("Versions", BsonNull.Value) ?? BsonNull.Value) +
                        ":related=" + VersionMetadata(effect.GetValue("RelatedVersions", BsonNull.Value));
                }));
    }

    public async Task AssertGitOpsRedisNamespaceRemovedAsync(GitOpsRedisNativeSnapshot native, CancellationToken ct)
    {
        await TestWait.UntilAsync(
            async token =>
            {
                try
                {
                    var current = await _kubernetes.CoreV1.ReadNamespaceAsync(
                        native.Namespace.Name,
                        cancellationToken: token);
                    VerifyOwnedNamespace(current, native.Namespace.Uid);
                    return false;
                }
                catch (HttpOperationException failure) when (failure.Response.StatusCode == HttpStatusCode.NotFound)
                {
                    return true;
                }
            },
            TimeSpan.FromMinutes(3),
            "The reviewed Redis/group removal did not finalize its native namespace.",
            ct);
    }

    private static GitOpsNativeIdentity NativeIdentity(V1ObjectMeta metadata)
    {
        Assert.False(string.IsNullOrWhiteSpace(metadata.Uid), "The native identity has no Kubernetes UID.");
        return new GitOpsNativeIdentity(metadata.Name, metadata.Uid);
    }

    private static void AssertNativeLabels(V1ObjectMeta metadata, IReadOnlyDictionary<string, string> expected)
    {
        foreach (var (key, value) in expected)
            Assert.Equal(value, metadata.Labels[key]);
    }

    private static string VersionMetadata(BsonValue versions) => versions is BsonArray documents
        ? string.Join(
            ",",
            documents.Take(24).Select(item => item["DocumentId"].AsString + "=" + item["Revision"].ToInt64()))
        : "[]";
}
