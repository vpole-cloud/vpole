// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed partial class DatabaseBrowserE2ETests
{
    [Fact]
    public async Task Tenant_can_restore_an_older_copy_and_a_selected_time_after_deleting_the_source()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(45));
        var ct = timeout.Token;
        using var evidence = new TestPhaseJournal(
            nameof(Tenant_can_restore_an_older_copy_and_a_selected_time_after_deleting_the_source)).Activate();
        var resources = new TestResourceScope("PostgreSQL backup API recovery", TimeSpan.FromMinutes(5), _timings);
        await resources.RunAsync(async () =>
        {
            // The fixture also checks native namespace ownership on cleanup if setup fails partway.
            fixture.EnableGitOpsNamespaceCleanup();
            var s3 = await fixture.CreateBackupTestStorageAsync(resources, ct);
            var group = await CreateGroupAsync("backup-" + Guid.NewGuid().ToString("N")[..12], ct);
            resources.Defer("delete backup resource group", token => DeleteGroupAsync(group, token));
            var profiles = await PrepareStorageProfileAsync("pole/postgresql", ct);
            resources.Defer(
                "disable backup test storage profile",
                token => PlatformTestProfiles.DisableStorageAsync(fixture, profiles.ProfileId, token));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var source = await BackupApiAsync(
                HttpMethod.Post,
                "/api/v1/postgres/",
                new
                {
                    name = "before-recovery", groupId = group, version = 17, instances = 1, storage = 1, password,
                    storageProfileId = profiles.ProfileId, placement = new { deploymentTargetId = profiles.TargetId },
                },
                ct);
            var id = source.GetProperty("id").GetString()!;
            resources.Defer("delete test source cluster", token => DeleteDatabaseAsync("postgres", id, token));
            await fixture.WaitForNamespaceCreationAsync(group, ct);
            await WaitForAppliedAsync("postgres", id, ct);
            await fixture.BackupSqlAsync(group, id, password, "postgres", "CREATE DATABASE user_data;", ct);
            await fixture.BackupSqlAsync(
                group,
                id,
                password,
                "user_data",
                "CREATE TABLE markers(id int PRIMARY KEY); INSERT INTO markers VALUES (1);",
                ct);
            var scope = source.GetProperty("placement").GetProperty("isolationScopeId").GetString();
            var access = await BackupApiAsync(
                HttpMethod.Post,
                "/api/v1/secrets",
                new
                {
                    name = "archive-access", purpose = "Barman archive", resourceGroupId = group,
                    isolationScopeId = scope,
                    values = new Dictionary<string, string> { ["access"] = s3.AccessKey, ["secret"] = s3.SecretKey },
                },
                ct);
            var secretId = access.GetProperty("id").GetString();
            resources.Defer(
                "delete archive credential",
                token => DeleteBackupResourceAsync($"/api/v1/secrets/{secretId}", token));
            var dest = await BackupApiAsync(
                HttpMethod.Post,
                "/api/v1/backups/destinations",
                new
                {
                    name = "archive", resourceGroupId = group, isolationScopeId = scope, s3.Endpoint,
                    bucket = "backups",
                    prefix = "tenant", credentialSecretId = secretId,
                    accessKeyIdKey = "access", secretAccessKeyKey = "secret",
                },
                ct);
            var destinationId = dest.GetProperty("id").GetString();
            resources.Defer(
                "delete archived destination",
                token => DeleteBackupResourceAsync(
                    $"/api/v1/backups/destinations/{destinationId}?expectedRevision=1",
                    token));
            var policy = await BackupApiAsync(
                HttpMethod.Post,
                "/api/v1/backups/policies",
                new
                {
                    name = "daily", resourceType = "pole/postgresql", resourceId = id,
                    destinationId, schedule = "5 2 * * *", suspended = true, retentionDays = 7,
                },
                ct);
            var policyId = policy.GetProperty("id").GetString()!;
            var archiveId = policy.GetProperty("archiveId").GetString()!;
            resources.Defer(
                "forget retired test archive",
                token => DeleteBackupResourceAsync($"/api/v1/backups/archives/{archiveId}?confirmed=true", token));
            resources.Defer(
                "retire backup policy",
                token => DeleteBackupResourceAsync($"/api/v1/backups/policies/{policyId}?expectedRevision=1", token));
            // Release the source before retiring its archive. The earlier registration covers partial setup.
            resources.Defer(
                "release source before retiring test archive",
                token => DeleteDatabaseAsync("postgres", id, token));
            var runPath = $"/api/v1/backups/policies/{policyId}/runs";
            var catalogPath = $"/api/v1/backups/archives/{archiveId}/backups";
            var firstKey = new { idempotencyKey = Guid.NewGuid().ToString("N") };
            var firstRun = await BackupApiAsync(HttpMethod.Post, runPath, firstKey, ct);
            var repeated = await BackupApiAsync(HttpMethod.Post, runPath, firstKey, ct);
            Assert.Equal(firstRun.GetProperty("id").GetString(), repeated.GetProperty("id").GetString());
            var first = await WaitForBackupOperationAsync(firstRun, ct, sourceGroup: group, sourceId: id);
            var firstId = first.GetProperty("backupId").GetString();
            Assert.False(string.IsNullOrEmpty(firstId));
            await VerifyArchiveCredentialRotationAsync(
                group,
                id,
                password,
                archiveId,
                secretId!,
                access.GetProperty("currentVersion").GetInt64(),
                s3,
                catalogPath,
                ct);
            var targetTime = await fixture.BackupSqlAsync(
                group,
                id,
                password,
                "user_data",
                """
                BEGIN;
                INSERT INTO markers VALUES (2);
                COMMIT;
                SELECT to_char(clock_timestamp() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"');
                """,
                ct);
            await fixture.BackupSqlAsync(
                group,
                id,
                password,
                "user_data",
                "INSERT INTO markers VALUES (3); SELECT pg_switch_wal();",
                ct);
            var second = await WaitForBackupOperationAsync(
                await BackupApiAsync(
                    HttpMethod.Post,
                    runPath,
                    new { idempotencyKey = Guid.NewGuid().ToString("N") },
                    ct),
                ct);
            Assert.NotEqual(firstId, second.GetProperty("backupId").GetString());
            await DeleteDatabaseAsync("postgres", id, ct);
            var catalog = await BackupApiAsync(HttpMethod.Get, catalogPath, null, ct);
            Assert.Contains(catalog.EnumerateArray(), item => item.GetProperty("backupId").GetString() == firstId);
            Assert.Contains(
                catalog.EnumerateArray(),
                item => item.GetProperty("backupId").GetString() == second.GetProperty("backupId").GetString());

            foreach (var useTime in new[] { false, true })
            {
                var operation = await BackupApiAsync(
                    HttpMethod.Post,
                    $"/api/v1/backups/archives/{archiveId}/restores",
                    new
                    {
                        idempotencyKey = Guid.NewGuid().ToString("N"),
                        name = useTime ? "at-selected-time" : "at-first-backup",
                        backupId = useTime ? null : firstId, targetTime = useTime ? targetTime : null, confirmed = true,
                        storageGiB = useTime ? 2 : (int?)null,
                    },
                    ct);
                var clone = operation.GetProperty("newResourceId").GetString()!;
                Assert.NotEqual(id, clone);
                resources.Defer(
                    "delete restored PostgreSQL cluster",
                    token => DeleteDatabaseAsync("postgres", clone, token));
                await WaitForBackupOperationAsync(operation, ct);
                var restored = await BackupApiAsync(HttpMethod.Get, $"/api/v1/postgres/{clone}", null, ct);
                Assert.Equal(useTime ? 2 : 1, restored.GetProperty("spec").GetProperty("storage").GetInt32());
                var credential = await BackupApiAsync(
                    HttpMethod.Get,
                    $"/api/v1/postgres/{clone}/connection-string",
                    null,
                    ct);
                var connection = new DbConnectionStringBuilder
                    { ConnectionString = credential.GetProperty("connectionString").GetString()! };
                var fresh = (string)connection["Password"];
                Assert.True(
                    fresh.Length > 0 && fresh != password,
                    "Recovery must issue a fresh administrator credential.");
                var rows = await fixture.BackupSqlAsync(
                    group,
                    clone,
                    fresh,
                    "user_data",
                    "SELECT string_agg(id::text, ',' ORDER BY id) FROM markers;",
                    ct);
                Assert.Equal(useTime ? "1,2" : "1", rows);
                await DeleteDatabaseAsync("postgres", clone, ct);
            }

            // The catalog contains eligible base copies, but the deleted source cannot have WAL reaching this time.
            var unreachable = DateTimeOffset.UtcNow.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                CultureInfo.InvariantCulture);
            var impossible = await BackupApiAsync(
                HttpMethod.Post,
                $"/api/v1/backups/archives/{archiveId}/restores",
                new
                {
                    idempotencyKey = Guid.NewGuid().ToString("N"), name = "unreachable", targetTime = unreachable,
                    confirmed = true,
                },
                ct);
            var incomplete = impossible.GetProperty("newResourceId").GetString()!;
            resources.Defer(
                "delete failed recovery cluster",
                token => DeleteDatabaseAsync("postgres", incomplete, token));
            await fixture.WaitForUnreachableRecoveryFailureAsync(group, incomplete, ct);
            var failed = await WaitForBackupOperationAsync(impossible, ct, expectedState: "Failed");
            Assert.Equal("restore-recovery-failed", failed.GetProperty("failureCode").GetString());
        });
    }

    private async Task VerifyArchiveCredentialRotationAsync(
        string group,
        string sourceId,
        string password,
        string archiveId,
        string secretId,
        long version,
        PoleE2EFixture.BackupTestStorage s3,
        string catalogPath,
        CancellationToken ct)
    {
        async Task<long> RotateAsync(long expected, string key)
        {
            var result = await BackupApiAsync(
                HttpMethod.Post,
                $"/api/v1/secrets/{secretId}/versions",
                new
                {
                    expectedVersion = expected,
                    values = new Dictionary<string, string> { ["access"] = s3.AccessKey, ["secret"] = key },
                },
                ct);
            var current = result.GetProperty("currentVersion").GetInt64();
            await fixture.WaitForBackupCredentialVersionAsync(group, archiveId, current, ct);
            return current;
        }

        var invalid = await RotateAsync(version, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Get,
            catalogPath,
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        using var denied = await fixture.Api.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        await fixture.WaitForWalArchiveHealthAsync(group, sourceId, password, healthy: false, ct);
        await RotateAsync(invalid, s3.SecretKey);
        await fixture.WaitForWalArchiveHealthAsync(group, sourceId, password, healthy: true, ct);
        var entries = await BackupApiAsync(HttpMethod.Get, catalogPath, null, ct);
        Assert.NotEmpty(entries.EnumerateArray());
    }

    private async Task<JsonElement> BackupApiAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(
            method,
            path,
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await fixture.Api.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Backup test {method} {path}: HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task DeleteBackupResourceAsync(string path, CancellationToken ct)
    {
        await TestWait.UntilAsync(
            async token =>
            {
                using var request = fixture.CreateAuthenticatedRequest(
                    HttpMethod.Delete,
                    path,
                    fixture.Alpha,
                    fixture.Alpha.DefaultTenantId);
                using var response = await fixture.Api.SendAsync(request, token);
                if (response.StatusCode == HttpStatusCode.Conflict)
                    return false; // A retained archive or Secret projection can be releasing asynchronously.
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return true;
                response.EnsureSuccessStatusCode();
                return true;
            },
            TimeSpan.FromMinutes(2),
            "Test archive lifecycle cleanup did not complete.",
            ct,
            TimeSpan.FromSeconds(2));
    }

    private async Task<JsonElement> WaitForBackupOperationAsync(
        JsonElement operation,
        CancellationToken ct,
        string expectedState = "Succeeded",
        string? sourceGroup = null,
        string? sourceId = null)
    {
        var id = operation.GetProperty("id").GetString();
        JsonElement result = default;
        await TestWait.UntilAsync(
            async token =>
            {
                result = await BackupApiAsync(HttpMethod.Get, $"/api/v1/backups/operations/{id}", null, token);
                var state = result.GetProperty("state").GetString();
                if (state is not ("Failed" or "Succeeded"))
                    return false;
                if (state != expectedState)
                {
                    var native = sourceGroup is null || sourceId is null
                        ? string.Empty
                        : await fixture.BackupFailureEvidenceAsync(sourceGroup, sourceId, id!, token);
                    throw new InvalidOperationException(
                        $"Expected backup operation {expectedState}, got {state} ({result.GetProperty("failureCode").GetString()}). {native}");
                }

                return true;
            },
            TimeSpan.FromMinutes(expectedState == "Failed" ? 20 : 10),
            "Backup operation did not finish.",
            ct,
            TimeSpan.FromSeconds(2));
        return result;
    }
}
