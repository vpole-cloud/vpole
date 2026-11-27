// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using k8s;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

public sealed partial class PoleE2EFixture
{
    public async Task AssertGitOpsAwaitingArtifactIsInertAsync(string webServiceId, CancellationToken ct)
    {
        var placement = await GitOpsPlacementAsync(webServiceId, ct);
        var ns = await GitOpsNamespaceAsync(placement, ct);
        var selector = $"pole.io/resource-deployment-id={placement["_id"].AsObjectId}";
        Assert.Empty(
            (await _kubernetes.AppsV1.ListNamespacedDeploymentAsync(ns, labelSelector: selector, cancellationToken: ct))
            .Items);
        Assert.Empty(
            (await _kubernetes.CoreV1.ListNamespacedPodAsync(ns, labelSelector: selector, cancellationToken: ct))
            .Items);
        Assert.Empty(
            (await _kubernetes.NetworkingV1.ListNamespacedIngressAsync(
                ns,
                labelSelector: selector,
                cancellationToken: ct)).Items);
    }

    public async Task RemoveGitOpsHostnameAttachmentAsync(string bindingId, CancellationToken ct)
    {
        var database = await GitOpsDatabaseAsync(ct);
        using var session = await database.Client.StartSessionAsync(cancellationToken: ct);
        await session.WithTransactionAsync(
            async (transaction, token) =>
            {
                var binding = await database.GetCollection<BsonDocument>("HostnameBindings")
                    .Find(
                        transaction,
                        new BsonDocument
                        {
                            { "_id", ObjectId.Parse(bindingId) },
                            { "TenantId", ObjectId.Parse(Alpha.DefaultTenantId) },
                        })
                    .SingleAsync(token);
                var result = await database.GetCollection<BsonDocument>("ResourceDeployments")
                    .UpdateOneAsync(
                        transaction,
                        new BsonDocument
                        {
                            { "_id", binding["ResourceDeploymentId"] },
                            { "TenantId", ObjectId.Parse(Alpha.DefaultTenantId) },
                        },
                        new BsonDocument
                        {
                            { "$pull", new BsonDocument("ResolvedPlacement.Ingress.Hostnames", binding["Hostname"]) },
                            { "$inc", new BsonDocument("Revision", 1L) },
                        },
                        cancellationToken: token);
                Assert.Equal(1, result.ModifiedCount);
                return true;
            },
            cancellationToken: ct);
    }

    public async Task AssertGitOpsDeliveryWiringAsync(
        IReadOnlyDictionary<string, string> ids,
        string digest,
        CancellationToken ct)
    {
        var placement = await GitOpsPlacementAsync(ids["WebService"], ct);
        var ns = await GitOpsNamespaceAsync(placement, ct);
        var redisPlacement = await GitOpsPlacementAsync(ids["RedisService"], ct);
        var redisNamespace = await GitOpsNamespaceAsync(redisPlacement, ct);
        var database = await GitOpsDatabaseAsync(ct);
        var binding = await database.GetCollection<BsonDocument>("HostnameBindings")
            .Find(
                new BsonDocument
                {
                    { "_id", ObjectId.Parse(ids["HostnameBinding"]) },
                    { "TenantId", ObjectId.Parse(Alpha.DefaultTenantId) },
                })
            .SingleAsync(ct);
        var hostname = binding["Hostname"].AsString;
        var selector = $"pole.io/resource-deployment-id={placement["_id"].AsObjectId}";
        await TestWait.UntilAsync(
            async token =>
            {
                var deployments = await _kubernetes.AppsV1.ListNamespacedDeploymentAsync(
                    ns,
                    labelSelector: selector,
                    cancellationToken: token);
                if (deployments.Items.Count != 1)
                    return false;

                var container = deployments.Items.Single().Spec.Template.Spec.Containers.Single();
                if (!container.Image.EndsWith($"@{digest}", StringComparison.Ordinal))
                    return false;

                Assert.Contains(
                    container.Env,
                    item => item.Name == "CACHE_HOST" &&
                        item.Value == $"mixed-redis-svc.{redisNamespace}.svc.cluster.local");
                Assert.Contains(container.Env, item => item.Name == "CACHE_PORT" && item.Value == "6379");
                var ingresses = await _kubernetes.NetworkingV1.ListNamespacedIngressAsync(
                    ns,
                    labelSelector: selector,
                    cancellationToken: token);
                return ingresses.Items.Any(ingress => ingress.Spec.Rules.Any(rule => rule.Host == hostname));
            },
            TimeSpan.FromMinutes(2),
            "GitOps artifact, connection environment and custom ingress",
            ct);
    }
}
