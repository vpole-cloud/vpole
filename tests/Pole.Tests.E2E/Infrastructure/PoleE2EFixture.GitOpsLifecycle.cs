using k8s;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

public sealed record GitOpsCleanupSnapshot(
    string[] Namespaces,
    string[] CredentialIds
);

public sealed partial class PoleE2EFixture
{
    public async Task<GitOpsCleanupSnapshot> CaptureGitOpsCleanupAsync(
        IReadOnlyDictionary<string, string> ids,
        CancellationToken ct)
    {
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in new[] { "WebService", "RedisService", "PostgresCluster", "FunctionApp" })
        {
            var placement = await GitOpsPlacementAsync(ids[kind], ct);
            var ns = await GitOpsNamespaceAsync(placement, ct);
            VerifyOwnedNamespace(await _kubernetes.CoreV1.ReadNamespaceAsync(ns, cancellationToken: ct));
            namespaces.Add(ns);
        }

        var claims = 0;
        var ingresses = 0;
        var policies = 0;
        foreach (var ns in namespaces)
        {
            claims += (await _kubernetes.CoreV1.ListNamespacedPersistentVolumeClaimAsync(ns, cancellationToken: ct))
                .Items.Count;
            ingresses += (await _kubernetes.NetworkingV1.ListNamespacedIngressAsync(ns, cancellationToken: ct)).Items
                .Count;
            policies += (await _kubernetes.NetworkingV1.ListNamespacedNetworkPolicyAsync(ns, cancellationToken: ct))
                .Items.Count;
        }

        Assert.True(claims >= 2, "The cleanup scenario requires real PostgreSQL and Redis persistent claims.");
        Assert.True(ingresses > 0, "The cleanup scenario requires a live ingress.");
        Assert.True(policies > 0, "The cleanup scenario requires live network policies.");
        var database = await GitOpsDatabaseAsync(ct);
        var credentials = await database.GetCollection<BsonDocument>("Secrets")
            .Find(
                new BsonDocument
                {
                    { "TenantId", Alpha.DefaultTenantId }, { "Management.ResourceId", ids["PostgresCluster"] },
                })
            .Project(new BsonDocument("_id", 1))
            .ToListAsync(ct);
        Assert.Single(credentials);
        return new GitOpsCleanupSnapshot(
            namespaces.Order(StringComparer.Ordinal).ToArray(),
            credentials.Select(item => item["_id"].AsString).ToArray());
    }

    public async Task AssertGitOpsCleanupCompletedAsync(
        GitOpsCleanupSnapshot before,
        IReadOnlyDictionary<string, string> ids,
        CancellationToken ct)
    {
        foreach (var ns in before.Namespaces)
            await TestWait.UntilAsync(
                async token => await ReadNamespaceIfPresentAsync(ns, token) is null,
                TimeSpan.FromMinutes(3),
                "The reviewed scope cleanup did not remove its native namespace.",
                ct);

        // Namespace absence includes pods, persistent claims, native credentials, ingress and network policies.
        // This checks the isolated native resources; it makes no claim about public DNS or TLS.
        var database = await GitOpsDatabaseAsync(ct);
        var deployments = new BsonDocument
        {
            { "TenantId", ObjectId.Parse(Alpha.DefaultTenantId) },
            {
                "ResourceId",
                new BsonDocument(
                    "$in",
                    new BsonArray(
                        new[] { "WebService", "RedisService", "PostgresCluster", "FunctionApp" }.Select(kind =>
                            new BsonObjectId(ObjectId.Parse(ids[kind])))))
            },
        };
        Assert.Equal(
            0,
            await database.GetCollection<BsonDocument>("ResourceDeployments")
                .CountDocumentsAsync(deployments, cancellationToken: ct));
        foreach (var credential in before.CredentialIds)
        {
            Assert.Equal(
                0,
                await database.GetCollection<BsonDocument>("Secrets")
                    .CountDocumentsAsync(
                        new BsonDocument { { "_id", credential }, { "TenantId", Alpha.DefaultTenantId } },
                        cancellationToken: ct));
            Assert.Equal(
                0,
                await database.GetCollection<BsonDocument>("SecretVersions")
                    .CountDocumentsAsync(
                        new BsonDocument { { "SecretId", credential }, { "TenantId", Alpha.DefaultTenantId } },
                        cancellationToken: ct));
        }
    }
}
