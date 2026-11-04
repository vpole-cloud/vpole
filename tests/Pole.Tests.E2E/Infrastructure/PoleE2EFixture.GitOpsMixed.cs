using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting.Testing;
using k8s;
using k8s.Autorest;
using k8s.Models;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

public sealed partial class PoleE2EFixture
{
    private bool _gitOpsNamespaceCleanup;
    private readonly List<FunctionSdkTestImage> _gitOpsImages = [];

    public void EnableGitOpsNamespaceCleanup() => _gitOpsNamespaceCleanup = true;

    public async Task<FunctionSdkTestImage> CreateGitOpsFunctionImageAsync(CancellationToken ct)
    {
        var image = await FunctionSdkTestImage.CreateAsync(_lab, _identity, ct);
        _gitOpsImages.Add(image);
        return image;
    }

    // These mutations target this fixture's disposable Mongo database and the authenticated test tenant.
    public async Task InjectMixedGitOpsDriftAsync(IReadOnlyDictionary<string, string> ids, CancellationToken ct)
    {
        var database = await GitOpsDatabaseAsync(ct);
        using var session = await database.Client.StartSessionAsync(cancellationToken: ct);
        await session.WithTransactionAsync(async (transaction, token) =>
        {
            foreach (var (kind, collection, fence) in new[]
                     {
                         ("WebService", "WebServices", "Revision"),
                         ("RedisService", "RedisServices", "Revision")
                     })
            {
                var filter = new BsonDocument { { "_id", ObjectId.Parse(ids[kind]) },
                    { "TenantId", MixedResourceTenant(kind) } };
                var update = new BsonDocument
                {
                    { "$set", new BsonDocument("Tags", new BsonArray { "controlled-drift" }) },
                    { "$inc", new BsonDocument(fence, 1L) }
                };
                var changed = await database.GetCollection<BsonDocument>(collection)
                    .UpdateOneAsync(transaction, filter, update, cancellationToken: token);
                Assert.Equal(1, changed.ModifiedCount);
            }

            var placement = new BsonDocument { { "ResourceId", ObjectId.Parse(ids["PostgresCluster"]) },
                { "TenantId", ObjectId.Parse(Alpha.DefaultTenantId) } };
            var drift = new BsonDocument { { "$set", new BsonDocument("FixedReplicas", 2) },
                { "$inc", new BsonDocument("Revision", 1L) } };
            var result = await database.GetCollection<BsonDocument>("ResourceDeployments")
                .UpdateOneAsync(transaction, placement, drift, cancellationToken: token);
            Assert.Equal(1, result.ModifiedCount);
            return true;
        }, cancellationToken: ct);
    }

    public async Task<string[]> ReadMixedGitOpsBusinessVersionsAsync(
        IReadOnlyDictionary<string, string> ids,
        CancellationToken ct)
    {
        var database = await GitOpsDatabaseAsync(ct);
        var versions = new List<string>();
        foreach (var (kind, collection, field) in new[]
                 {
                     ("WebService", "WebServices", "ActiveRevision"),
                     ("RedisService", "RedisServices", "Revision"),
                     ("PostgresCluster", "Postgres", "Revision"),
                     ("FunctionApp", "FunctionApps", "NextRevisionNumber")
                 })
        {
            var filter = new BsonDocument { { "_id", ObjectId.Parse(ids[kind]) },
                { "TenantId", MixedResourceTenant(kind) } };
            var document = await database.GetCollection<BsonDocument>(collection).Find(filter)
                .Project(new BsonDocument(field, 1)).SingleAsync(ct);
            versions.Add($"{kind}:{document[field].ToInt64()}");
        }

        return versions.ToArray();
    }

    public async Task AssertGitOpsPreparedWorkloadsAreInertAsync(
        IReadOnlyDictionary<string, string> ids,
        CancellationToken ct)
    {
        var database = await GitOpsDatabaseAsync(ct);
        foreach (var (kind, collection) in new[] { ("WebService", "WebServices"),
                     ("PostgresCluster", "Postgres"), ("FunctionApp", "FunctionApps") })
        {
            var filter = new BsonDocument { { "_id", ObjectId.Parse(ids[kind]) },
                { "TenantId", MixedResourceTenant(kind) } };
            Assert.Equal(1, await database.GetCollection<BsonDocument>(collection).CountDocumentsAsync(filter,
                cancellationToken: ct));
            var placement = await GitOpsPlacementAsync(ids[kind], ct);
            var ns = await GitOpsNamespaceAsync(placement, ct);
            var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync(ns,
                labelSelector: $"pole.io/resource-deployment-id={placement["_id"].AsObjectId}", cancellationToken: ct);
            Assert.Empty(pods.Items);
        }
    }

    public async Task VerifyGitOpsPostgresBootstrapAsync(
        string clusterId,
        string expectedPassword,
        CancellationToken ct)
    {
        var placement = await GitOpsPlacementAsync(clusterId, ct);
        var ns = await GitOpsNamespaceAsync(placement, ct);
        VerifyOwnedNamespace(await _kubernetes.CoreV1.ReadNamespaceAsync(ns, cancellationToken: ct));
        var name = $"pg-{clusterId}";
        V1StatefulSet? statefulSet = null;
        await TestWait.UntilAsync(async phase =>
        {
            try
            {
                statefulSet = await _kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(
                    name, ns, cancellationToken: phase);
                var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync(ns,
                    labelSelector: $"pole.io/resource-deployment-id={placement["_id"].AsObjectId}",
                    cancellationToken: phase);
                var pod = pods.Items.SingleOrDefault(item => item.Metadata.Name == name + "-0");
                if (pod?.Status?.Phase != "Running" || string.IsNullOrWhiteSpace(pod.Status.PodIP))
                    return false;

                var valid = new PostgresLoginProbe(ns, pod.Metadata.Name, pod.Status.PodIP, expectedPassword);
                if (await VerifyPostgresLoginAsync(valid, phase) != PostgresLoginResult.Authenticated)
                    return false;

                var invalid = valid with { Password = "invalid-stage-five-bootstrap-password" };
                Assert.Equal(PostgresLoginResult.PasswordRejected, await VerifyPostgresLoginAsync(invalid, phase));
                Assert.Equal(PostgresLoginResult.Authenticated, await VerifyPostgresLoginAsync(valid, phase));
                return true;
            }
            catch (HttpOperationException failure) when (failure.Response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }, TimeSpan.FromMinutes(3), "The GitOps PostgreSQL workload did not accept its bootstrap credential.", ct,
            TimeSpan.FromSeconds(2));

        var reference = statefulSet!.Spec.Template.Spec.Containers.Single(container => container.Name == "postgres")
            .Env.Single(variable => variable.Name == "POSTGRES_PASSWORD").ValueFrom.SecretKeyRef;
        var secret = await _kubernetes.CoreV1.ReadNamespacedSecretAsync(reference.Name, ns, cancellationToken: ct);
        Assert.True(CryptographicOperations.FixedTimeEquals(SHA256.HashData(secret.Data[reference.Key]),
            SHA256.HashData(Encoding.UTF8.GetBytes(expectedPassword))),
            "The native credential does not match bootstrap.");
    }

    private BsonValue MixedResourceTenant(string kind) => kind == "PostgresCluster"
        ? new BsonObjectId(ObjectId.Parse(Alpha.DefaultTenantId))
        : new BsonString(Alpha.DefaultTenantId);

    private sealed record PostgresLoginProbe(
        string Namespace,
        string Pod,
        string Host,
        string Password);

    private enum PostgresLoginResult { Authenticated, PasswordRejected, Unavailable }

    private async Task<PostgresLoginResult> VerifyPostgresLoginAsync(PostgresLoginProbe probe, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_kubernetesTarget.KubectlPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in _kubernetesTarget.KubectlSelectionArguments)
            process.StartInfo.ArgumentList.Add(argument);
        foreach (var argument in new[] { "exec", "-i", "-n", probe.Namespace, probe.Pod, "--", "sh", "-c",
                     "IFS= read -r PGPASSWORD; export PGPASSWORD; " +
                     "exec psql -h \"$1\" -U app -d postgres -tAc 'select current_user'",
                     "bootstrap-check", probe.Host })
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            // The credential is sent only through stdin, never an argument or diagnostic.
            await process.StandardInput.WriteLineAsync(probe.Password.AsMemory(), ct);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            var error = await errors;
            if (process.ExitCode == 0 && (await output).Trim() == "app")
                return PostgresLoginResult.Authenticated;

            return error.Contains("password authentication failed", StringComparison.Ordinal)
                ? PostgresLoginResult.PasswordRejected
                : PostgresLoginResult.Unavailable;
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private async Task<IMongoDatabase> GitOpsDatabaseAsync(CancellationToken ct)
    {
        var app = _application ?? throw new InvalidOperationException("The E2E graph is not initialized.");
        var connection = await app.GetConnectionStringAsync("pole-database", ct);
        return new MongoClient(connection).GetDatabase("pole-database");
    }

    private async Task<BsonDocument> GitOpsPlacementAsync(string resourceId, CancellationToken ct)
    {
        var database = await GitOpsDatabaseAsync(ct);
        var filter = new BsonDocument { { "ResourceId", ObjectId.Parse(resourceId) },
            { "TenantId", ObjectId.Parse(Alpha.DefaultTenantId) } };
        return await database.GetCollection<BsonDocument>("ResourceDeployments").Find(filter).SingleAsync(ct);
    }

    private async Task<string> GitOpsNamespaceAsync(BsonDocument placement, CancellationToken ct)
    {
        var database = await GitOpsDatabaseAsync(ct);
        var filter = new BsonDocument { { "_id", placement["RuntimeCellId"] },
            { "TenantId", ObjectId.Parse(Alpha.DefaultTenantId) } };
        var cell = await database.GetCollection<BsonDocument>("RuntimeCells").Find(filter)
            .Project(new BsonDocument("KubernetesNamespace", 1)).SingleAsync(ct);
        return cell["KubernetesNamespace"].AsString;
    }

    private async Task CleanupGitOpsNamespacesAsync(CancellationToken ct)
    {
        if (!_gitOpsNamespaceCleanup)
            return;

        // Failure cleanup stops controllers first, then deletes only this exact fixture's labelled namespaces
        // with UID preconditions. Successful lifecycle scenarios use the production reviewed deletion flow.
        var selector = $"{TestRunIdentity.OwnerLabel}={TestRunIdentity.Owner}," +
            $"{TestRunIdentity.WorktreeLabel}={_identity.WorktreeId},{TestRunIdentity.RunLabel}={_identity.RunId}";
        var namespaces = await _kubernetes.CoreV1.ListNamespaceAsync(labelSelector: selector, cancellationToken: ct);
        await using var cleanup = new TestResourceScope("GitOps fixture namespaces");
        foreach (var ns in namespaces.Items)
        {
            cleanup.Defer(ns.Metadata.Name, async token =>
            {
                var current = await ReadNamespaceIfPresentAsync(ns.Metadata.Name, token);
                if (current is null)
                    return;

                VerifyOwnedNamespace(current, ns.Metadata.Uid);
                try
                {
                    await _kubernetes.CoreV1.DeleteNamespaceAsync(ns.Metadata.Name,
                        body: new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = ns.Metadata.Uid } },
                        cancellationToken: token);
                }
                catch (HttpOperationException failure) when (failure.Response.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }

                await TestWait.UntilAsync(async phase =>
                    await ReadNamespaceIfPresentAsync(ns.Metadata.Name, phase) is null,
                    TimeSpan.FromMinutes(2), "The owned GitOps namespace did not terminate.", token);
            });
        }
    }

    private async Task DisposeGitOpsImagesAsync()
    {
        await using var cleanup = new TestResourceScope("GitOps fixture images");
        foreach (var image in _gitOpsImages)
            cleanup.Defer(image.Repository, _ => image.DisposeAsync().AsTask());
        _gitOpsImages.Clear();
    }
}
