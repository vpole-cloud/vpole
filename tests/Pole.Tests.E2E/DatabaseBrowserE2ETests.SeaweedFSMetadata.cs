using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed partial class DatabaseBrowserE2ETests
{
    [Fact]
    public async Task Owner_can_select_existing_HA_Postgres_for_SeaweedFS_and_purge_only_its_metadata()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        var ct = timeout.Token;
        using var evidence = new TestPhaseJournal(
            nameof(Owner_can_select_existing_HA_Postgres_for_SeaweedFS_and_purge_only_its_metadata)).Activate();
        var resources = new TestResourceScope("Existing PostgreSQL SeaweedFS", TimeSpan.FromMinutes(18), _timings);
        await resources.RunAsync(async () =>
        {
            fixture.EnableGitOpsNamespaceCleanup();
            var name = $"meta-{Guid.NewGuid():N}"[..15];
            var hostGroup = await CreateGroupAsync(name + "-host", ct);
            resources.Defer("delete PostgreSQL host group", token => DeleteGroupAsync(hostGroup, token));
            var postgresProfile = await PrepareStorageProfileAsync("pole/postgresql", ct);
            resources.Defer(
                "disable PostgreSQL profile",
                token => PlatformTestProfiles.DisableStorageAsync(fixture, postgresProfile.ProfileId, token));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var postgres = await BackupApiAsync(
                HttpMethod.Post,
                "/api/v1/postgres/",
                new
                {
                    name = name + "-pg", groupId = hostGroup, version = 17, instances = 3, storage = 1,
                    replicationMode = "SynchronousHa", password, storageProfileId = postgresProfile.ProfileId,
                    placement = new { deploymentTargetId = postgresProfile.TargetId },
                },
                ct);
            var postgresId = postgres.GetProperty("id").GetString()!;
            resources.Defer("delete PostgreSQL host", token => DeleteDatabaseAsync("postgres", postgresId, token));
            await fixture.WaitForNamespaceCreationAsync(hostGroup, ct);
            await WaitForAppliedAsync("postgres", postgresId, ct);
            output.WriteLine("Existing metadata host applied synchronous HA configuration.");
            await fixture.BackupSqlAsync(hostGroup, postgresId, password, "postgres", "CREATE DATABASE user_data;", ct);
            await fixture.BackupSqlAsync(
                hostGroup,
                postgresId,
                password,
                "postgres",
                "REVOKE CONNECT, CREATE, TEMPORARY ON DATABASE user_data FROM PUBLIC;",
                ct);
            var marker = Guid.NewGuid().ToString("N");
            await fixture.BackupSqlAsync(
                hostGroup,
                postgresId,
                password,
                "user_data",
                $"CREATE TABLE markers(value text PRIMARY KEY); INSERT INTO markers VALUES ('{marker}');",
                ct);
            var originalCluster = (await fixture.ReadMetadataTestResourcesAsync(hostGroup, "clusters", ct))
                .GetProperty("items")
                .EnumerateArray()
                .Single();
            var originalUid = originalCluster.GetProperty("metadata").GetProperty("uid").GetString();
            var candidates = await BackupApiAsync(
                HttpMethod.Get,
                $"/api/v1/postgres/managed-database-candidates?deploymentTargetId={postgresProfile.TargetId}",
                null,
                ct);
            var candidate = Assert.Single(
                candidates.EnumerateArray(),
                item => item.GetProperty("id").GetString() == postgresId);
            Assert.Equal(3, candidate.GetProperty("instances").GetInt32());
            Assert.Equal("SynchronousHa", candidate.GetProperty("replicationMode").GetString());

            var group = await CreateGroupAsync(name + "-weed", ct);
            resources.Defer("delete SeaweedFS consumer group", token => DeleteGroupAsync(group, token));
            var placement = await PrepareStorageProfileAsync("pole/seaweedfs", ct);
            resources.Defer(
                "disable SeaweedFS profile",
                token => PlatformTestProfiles.DisableStorageAsync(fixture, placement.ProfileId, token));
            Assert.Equal(postgresProfile.TargetId, placement.TargetId);
            var baseline = await BackupApiAsync(HttpMethod.Get, "/api/v1/tenant-quotas/usage", null, ct);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.ConnectAsync(
                await fixture.GetBrowserEndpointAsync(ct),
                new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var context
                = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
            var page = await BrowserEvidence.NewPageAsync(context);
            await LoginAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "SeaweedFS", Exact = true }).ClickAsync();
            var content = page.Locator("#app-content");
            ILocator Button(string label) => content.GetByRole(AriaRole.Button, new() { Name = label, Exact = true });
            await Button("New SeaweedFS instance").ClickAsync();
            await content.GetByLabel("Resource group").SelectOptionAsync(group);
            await content.GetByLabel("Deployment target").SelectOptionAsync(placement.TargetId);
            await content.GetByLabel("Storage profile").SelectOptionAsync(placement.ProfileId);
            await content.GetByLabel("Name", new() { Exact = true }).FillAsync(name);
            await content.GetByLabel("Deployment mode").SelectOptionAsync("Replicated");
            await content.GetByLabel("Filer metadata storage").SelectOptionAsync("existing");
            var selector = content.GetByLabel("PostgreSQL cluster", new() { Exact = true });
            await Assertions.Expect(selector.Locator($"option[value='{postgresId}']")).ToHaveCountAsync(1);
            await selector.SelectOptionAsync(postgresId);
            var accepted = await page.RunAndWaitForResponseAsync(
                () => Button("Create SeaweedFS").ClickAsync(),
                r => r.Request.Method == "POST" && new Uri(r.Url).AbsolutePath.TrimEnd('/') == "/api/v1/seaweedfs");
            Assert.Equal(202, accepted.Status);
            var id = (await accepted.JsonAsync())!.Value.GetProperty("seaweedFSId").GetString()!;
            resources.Defer("purge SeaweedFS before releasing host", token => PurgeBrowserSeaweedFSAsync(id, token));
            await fixture.WaitForNamespaceCreationAsync(group, ct);
            await WaitForSeaweedFSAsync(id, ct, TimeSpan.FromMinutes(12));
            output.WriteLine("Replicated SeaweedFS applied using the selected PostgreSQL host.");
            var instance = await BackupApiAsync(HttpMethod.Get, $"/api/v1/seaweedfs/{id}", null, ct);
            Assert.Equal(postgresId, instance.GetProperty("spec").GetProperty("existingPostgresId").GetString());
            var role = (await fixture.ReadMetadataTestResourcesAsync(hostGroup, "databaseroles", ct))
                .GetProperty("items")
                .EnumerateArray()
                .Single();
            var database = (await fixture.ReadMetadataTestResourcesAsync(hostGroup, "databases", ct))
                .GetProperty("items")
                .EnumerateArray()
                .Single();
            var roleSpec = role.GetProperty("spec");
            var username = roleSpec.GetProperty("name").GetString()!;
            var databaseName = database.GetProperty("spec").GetProperty("name").GetString()!;
            Assert.Matches("^pole_role_[0-9a-f]{24}$", username);
            Assert.Matches("^pole_db_[0-9a-f]{24}$", databaseName);
            Assert.Equal(username, database.GetProperty("spec").GetProperty("owner").GetString());
            Assert.Equal("pg-" + postgresId, roleSpec.GetProperty("cluster").GetProperty("name").GetString());
            foreach (var flag in new[] { "superuser", "createdb", "createrole", "replication", "bypassrls", "inherit" })
                Assert.False(roleSpec.GetProperty(flag).GetBoolean());
            var credentialName = roleSpec.GetProperty("passwordSecret").GetProperty("name").GetString()!;
            Assert.True(await fixture.MetadataCredentialExistsAsync(hostGroup, credentialName, ct));
            Assert.True(
                await fixture.VerifyMetadataDatabaseIsolationAsync(
                    hostGroup,
                    postgresId,
                    credentialName,
                    databaseName,
                    "user_data",
                    ct),
                "The managed credential must authenticate to its own database and be denied the private sibling.");
            Assert.Equal(
                "t",
                await fixture.BackupSqlAsync(
                    hostGroup,
                    postgresId,
                    password,
                    "postgres",
                    $"SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='{username}' AND rolcanlogin " +
                    "AND NOT (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls OR rolinherit)) " +
                    $"AND EXISTS (SELECT 1 FROM pg_database WHERE datname='{databaseName}' " +
                    $"AND pg_get_userbyid(datdba)='{username}') " + "AND NOT EXISTS (SELECT 1 FROM pg_database d, " +
                    "LATERAL aclexplode(coalesce(d.datacl, acldefault('d', d.datdba))) a " +
                    $"WHERE d.datname='{databaseName}' AND a.grantee=0 " +
                    "AND a.privilege_type IN ('CONNECT','CREATE','TEMPORARY'));",
                    ct));
            await AssertOnlyExistingMetadataClusterAsync(hostGroup, group, originalUid, ct);
            var quota = await BackupApiAsync(HttpMethod.Get, "/api/v1/tenant-quotas/usage", null, ct);
            Assert.Equal(MetadataQuota(baseline, "WorkloadInstances") + 12, MetadataQuota(quota, "WorkloadInstances"));
            Assert.Equal(
                MetadataQuota(baseline, "SeaweedFSServiceCount") + 1,
                MetadataQuota(quota, "SeaweedFSServiceCount"));
            Assert.Equal(MetadataQuota(baseline, "SeaweedFSStorageGb") + 6, MetadataQuota(quota, "SeaweedFSStorageGb"));
            await AssertMetadataHostProtectedAsync(postgresId, ct);
            output.WriteLine("Dedicated role, database, credentials, capacity and host lifecycle protection verified.");

            var bucket = await BackupApiAsync(
                HttpMethod.Post,
                $"/api/v1/seaweedfs/{id}/buckets",
                new { name = "objects" },
                ct);
            var bucketId = bucket.GetProperty("id").GetString();
            await WaitForSeaweedFSAsync(id, ct);
            var keys = await BackupApiAsync(
                HttpMethod.Get,
                $"/api/v1/seaweedfs/{id}/buckets/{bucketId}/credentials",
                null,
                ct);
            await using (var gateway = await fixture.ForwardSeaweedFSForTestAsync(group, id, ct))
            using (var s3 = new AmazonS3Client(
                       new BasicAWSCredentials(
                           keys.GetProperty("accessKey").GetString(),
                           keys.GetProperty("secretKey").GetString()),
                       new AmazonS3Config
                       {
                           ServiceURL = $"http://127.0.0.1:{gateway.LocalPort}", AuthenticationRegion = "us-east-1",
                           ForcePathStyle = true,
                       }))
            {
                var value = Guid.NewGuid().ToString();
                await s3.PutObjectAsync(
                    new PutObjectRequest { BucketName = "objects", Key = "metadata-proof.txt", ContentBody = value },
                    ct);
                using var result = await s3.GetObjectAsync("objects", "metadata-proof.txt", ct);
                using var reader = new StreamReader(result.ResponseStream);
                Assert.Equal(value, await reader.ReadToEndAsync(ct));
            }

            var filers = (await fixture.ReadMetadataTestResourcesAsync(group, "pods", ct)).GetProperty("items")
                .EnumerateArray()
                .Where(p => p.GetProperty("metadata")
                        .GetProperty("labels")
                        .TryGetProperty("app.kubernetes.io/component", out var component) &&
                    component.GetString() == "filer")
                .Select(p => p.GetProperty("status").GetProperty("podIP").GetString()!)
                .ToArray();
            Assert.Equal(3, filers.Length);
            var addresses = string.Join(",", filers.Select(address => $"'{address}'::inet"));
            Assert.Equal(
                "t",
                await fixture.BackupSqlAsync(
                    hostGroup,
                    postgresId,
                    password,
                    "postgres",
                    $"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname='{databaseName}' AND " +
                    $"usename='{username}' AND client_addr IN ({addresses}));",
                    ct));
            Assert.NotEqual(
                "0",
                await fixture.BackupSqlAsync(
                    hostGroup,
                    postgresId,
                    password,
                    databaseName,
                    "SELECT count(*) FROM information_schema.tables WHERE table_schema='public';",
                    ct));
            output.WriteLine("Signed S3 write/read and live filer connections to the dedicated database verified.");

            // Use the normal soft-delete and configured retention path, keeping PostgreSQL alive throughout.
            await PurgeBrowserSeaweedFSAsync(id, ct);
            await AssertOnlyExistingMetadataClusterAsync(hostGroup, group, originalUid, ct);
            Assert.Empty(
                (await fixture.ReadMetadataTestResourcesAsync(hostGroup, "databaseroles", ct)).GetProperty("items")
                .EnumerateArray());
            Assert.Empty(
                (await fixture.ReadMetadataTestResourcesAsync(hostGroup, "databases", ct)).GetProperty("items")
                .EnumerateArray());
            await TestWait.UntilAsync(
                async token => !await fixture.MetadataCredentialExistsAsync(hostGroup, credentialName, token),
                TimeSpan.FromMinutes(2),
                "The purged metadata credential projection remained present.",
                ct);
            Assert.Equal(
                "0|0",
                await fixture.BackupSqlAsync(
                    hostGroup,
                    postgresId,
                    password,
                    "postgres",
                    $"SELECT (SELECT count(*) FROM pg_database WHERE datname='{databaseName}'), " +
                    $"(SELECT count(*) FROM pg_roles WHERE rolname='{username}');",
                    ct));
            Assert.Equal(
                marker,
                await fixture.BackupSqlAsync(
                    hostGroup,
                    postgresId,
                    password,
                    "user_data",
                    "SELECT value FROM markers;",
                    ct));
            var remaining = await BackupApiAsync(HttpMethod.Get, $"/api/v1/postgres/{postgresId}", null, ct);
            Assert.Equal(3, remaining.GetProperty("spec").GetProperty("instances").GetInt32());
            var restoredQuota = await BackupApiAsync(HttpMethod.Get, "/api/v1/tenant-quotas/usage", null, ct);
            foreach (var key in new[]
                     {
                         "WorkloadInstances", "SeaweedFSServiceCount", "SeaweedFSStorageGb",
                         "CpuMillis", "MemoryMi"
                     })
                Assert.Equal(MetadataQuota(baseline, key), MetadataQuota(restoredQuota, key));
            // Successful normal host deletion also proves the managed claim released its lifecycle protection.
            await DeleteDatabaseAsync("postgres", postgresId, ct);
            output.WriteLine("Retention purge preserved the host and unrelated data, and released the metadata claim.");
        });
    }

    private async Task AssertMetadataHostProtectedAsync(string postgresId, CancellationToken ct)
    {
        foreach (var method in new[] { HttpMethod.Delete, HttpMethod.Put })
        {
            var current = await BackupApiAsync(HttpMethod.Get, $"/api/v1/postgres/{postgresId}", null, ct);
            using var request = fixture.CreateAuthenticatedRequest(
                method,
                $"/api/v1/postgres/{postgresId}",
                fixture.Alpha,
                fixture.Alpha.DefaultTenantId);
            if (method == HttpMethod.Put)
                request.Content = JsonContent.Create(
                    new
                    {
                        expectedRevision = current.GetProperty("revision").GetInt64(), instances = 1,
                        replicationMode = "Asynchronous"
                    });
            using var rejected = await fixture.Api.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        }
    }

    private async Task AssertOnlyExistingMetadataClusterAsync(
        string hostGroup,
        string consumerGroup,
        string? originalUid,
        CancellationToken ct)
    {
        var clusters = (await fixture.ReadMetadataTestResourcesAsync(hostGroup, "clusters", ct)).GetProperty("items")
            .EnumerateArray()
            .ToArray();
        Assert.Single(clusters);
        Assert.Equal(originalUid, clusters[0].GetProperty("metadata").GetProperty("uid").GetString());
        Assert.Empty(
            (await fixture.ReadMetadataTestResourcesAsync(consumerGroup, "clusters", ct)).GetProperty("items")
            .EnumerateArray());
    }

    private static long MetadataQuota(JsonElement usage, string key) => usage.GetProperty("committedUsage")
        .EnumerateArray()
        .Single(item => item.GetProperty("key").GetString() == key)
        .GetProperty("value")
        .GetInt64();
}
