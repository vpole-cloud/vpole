using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed partial class GitOpsRepositoryFlowE2ETests
{
    [Fact]
    public async Task ConfirmedRedisReplacementChangesNativeIdentityAndWaitsForReviewedCleanup()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var evidence = new TestPhaseJournal(
            nameof(ConfirmedRedisReplacementChangesNativeIdentityAndWaitsForReviewedCleanup)).Activate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var ct = timeout.Token;
        fixture.EnableGitOpsNamespaceCleanup();
        const string group = "gitops-replace-group";
        const string name = "gitops-replacement-redis";
        const string profile = "gitops-replacement-profile";
        var target = await CreateMixedCatalogAsync(ct, profile);
        var connection = await ConnectAsync(ct);
        var builds = await fixture.CountGitOpsRedisScenarioBuildJobsAsync(ct);
        var commit = new string('9', 40);
        await PublishRedisReplacementAsync(commit, RedisReplacementFiles(group, name, target, profile), ct);
        var created = await JsonAsync(HttpMethod.Post, Root + "/", new
        {
            idempotencyKey = Guid.NewGuid(),
            provider = "GitHub",
            connectionId = connection,
            externalRepositoryId = "9005",
            branch = "main",
            manifestDirectory = "manifests",
            pollIntervalSeconds = 300
        }, ct);
        var source = created.GetProperty("id").GetString()!;
        var applied = await WaitForRedisLifecycleRunAsync(source, null, ct);
        Assert.Equal(commit, applied.GetProperty("synchronization").GetProperty("fullyAppliedCommit").GetString());
        var operations = await OperationsAsync(source, LatestRunId(applied), ct);
        var oldId = Assert.Single(operations.GetProperty("items").EnumerateArray().Where(item =>
                item.GetProperty("kind").GetString() == "RedisService")
            .Select(item => item.GetProperty("resourceId").GetString()!).Distinct(StringComparer.Ordinal));
        var before = await fixture.WaitForGitOpsRedisNativeAsync(oldId, ct);
        await LifecycleActivityAsync(source, "pause", applied, ct);

        // A compatible same-address replacement still requires a concrete server-reserved new identity.
        var preview = await LifecyclePreviewAsync(source, new
        {
            intent = "Replacement",
            expectedRevision = await RevisionOfAsync(source, ct),
            replacements = new[]
            {
                new { original = new { kind = "RedisService", name, resourceId = oldId },
                    desired = new { kind = "RedisService", name } }
            }
        }, ct);
        var action = Assert.Single(preview.GetProperty("actions").GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("action").GetString() == "Replace");
        var newId = action.GetProperty("replacement").GetProperty("resourceId").GetString()!;
        Assert.NotEqual(oldId, newId);
        var warnings = action.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("ProtectedData", warnings);
        Assert.Contains("DataMayBeLost", warnings);
        Assert.Contains("IdentityWillChange", warnings);
        Assert.Contains("Downtime", warnings);
        Assert.Contains("RedisPersistentVolumeClaimsDeleted", warnings);
        Assert.Contains("PersistentVolumeReclaimPolicyDeterminesDataRetention", warnings);
        var replacementRun = await ConfirmRedisReplacementAsync(source, preview, ct);
        await WaitForRedisLifecycleRunAsync(source, replacementRun, ct, "e2e.gitops.lifecycle.replace");
        await ExpectLifecycleStatusAsync(HttpMethod.Get, $"/api/v1/redis-services/{oldId}", null,
            HttpStatusCode.NotFound, ct);
        var after = await fixture.WaitForGitOpsRedisNativeAsync(newId, ct);
        await fixture.AssertGitOpsRedisNativeReplacedAsync(before, after, ct);
        await fixture.AssertGitOpsRedisReplacementRecordedAsync(source, replacementRun, oldId, newId, ct);
        operations = await OperationsAsync(source, replacementRun, ct);
        Assert.All(operations.GetProperty("items").EnumerateArray(), item =>
            Assert.Equal("Succeeded", item.GetProperty("status").GetString()));
        var removal = Assert.Single(operations.GetProperty("items").EnumerateArray(), item =>
            item.TryGetProperty("lifecycle", out var lifecycle) && lifecycle.ValueKind != JsonValueKind.Null &&
            lifecycle.GetProperty("action").GetString() == "Replace");
        var lifecycle = removal.GetProperty("lifecycle");
        Assert.Equal(newId, lifecycle.GetProperty("replacementResourceId").GetString());
        Assert.Equal("Apply", lifecycle.GetProperty("lastCompletedStep").GetString());

        // Empty input cannot grant deletion authority by itself, even after the source opts into Prune.
        await JsonAsync(HttpMethod.Put, $"{Root}/{source}", new
        {
            expectedRevision = await RevisionOfAsync(source, ct),
            pollIntervalSeconds = 300,
            prune = "Prune"
        }, ct);
        await PublishRedisReplacementAsync(new string('0', 40),
            [new { path = "manifests/.keep", content = "" }], ct);
        var cleanup = await LifecyclePreviewAsync(source, new
        {
            intent = "ResourceChanges",
            expectedRevision = await RevisionOfAsync(source, ct)
        }, ct);
        Assert.All(cleanup.GetProperty("actions").GetProperty("items").EnumerateArray(), item =>
            Assert.Equal("Remove", item.GetProperty("action").GetString()));
        Assert.Contains(cleanup.GetProperty("actions").GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("resource").GetProperty("resourceId").GetString() == newId &&
            item.GetProperty("warnings").EnumerateArray().Any(warning => warning.GetString() == "ProtectedData"));
        var cleanupRun = await ConfirmRedisReplacementAsync(source, cleanup, ct);
        var cleaned = await WaitForRedisLifecycleRunAsync(source, cleanupRun, ct, "e2e.gitops.lifecycle.cleanup");
        Assert.Equal(0, cleaned.GetProperty("inventory").GetProperty("owned").GetInt64());
        using (_timings.Measure("e2e.gitops.native.cleanup"))
            await fixture.AssertGitOpsRedisNamespaceRemovedAsync(after, ct);
        Assert.Equal(builds, await fixture.CountGitOpsRedisScenarioBuildJobsAsync(ct));
    }

    private async Task<string> ConfirmRedisReplacementAsync(string source, JsonElement preview, CancellationToken ct)
    {
        var scheduled = await JsonAsync(HttpMethod.Post, PreviewPath(source, preview) + "/confirm",
            new { expectedRevision = preview.GetProperty("revision").GetInt64() }, ct);
        return scheduled.GetProperty("runId").GetString()!;
    }

    private async Task<JsonElement> WaitForRedisLifecycleRunAsync(
        string source,
        string? runId,
        CancellationToken ct,
        string timingPhase = "e2e.gitops.lifecycle.wait")
    {
        var current = await WaitAsync(source, item =>
        {
            if (runId is not null && LatestRunId(item) != runId)
                return false;
            return item.GetProperty("status").GetString() is "ConfigurationApplied" or "Blocked" or "Failed";
        }, ct, timingPhase);
        if (current.GetProperty("status").GetString() is "Blocked" or "Failed")
        {
            var codes = current.GetProperty("latestRun").GetProperty("diagnostics").EnumerateArray()
                .Select(diagnostic => diagnostic.GetProperty("code").GetString() + "@" +
                    diagnostic.GetProperty("field").GetString());
            var receipts = await fixture.ReadGitOpsRedisReceiptMetadataAsync(source, LatestRunId(current), ct);
            var operations = await OperationsAsync(source, LatestRunId(current), ct);
            var statuses = operations.GetProperty("items").EnumerateArray().Take(20).Select(item =>
                item.GetProperty("kind").GetString() + "/" + item.GetProperty("phase").GetRawText() + ":" +
                item.GetProperty("status").GetString());
            Assert.Fail("Reviewed Redis lifecycle stopped: " + string.Join(", ", codes) +
                        "; operations=" + string.Join(", ", statuses) + "; receipts=" + receipts);
        }
        return current;
    }

    private async Task PublishRedisReplacementAsync(string commit, object[] files, CancellationToken ct)
    {
        using var published = await fixture.Stub.PostAsJsonAsync("/__e2e/gitops-redis-replacement",
            new { commit, files }, ct);
        Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);
    }

    private static object[] RedisReplacementFiles(string group, string name, string target, string profile)
    {
        object File(string kind, string resourceName, object spec) => new
        {
            path = $"manifests/{kind}-{resourceName}.yaml",
            content = JsonSerializer.Serialize(new
            {
                apiVersion = "pole.io/v1alpha1",
                kind,
                metadata = new { name = resourceName },
                spec
            })
        };
        return
        [
            File("ResourceGroup", group, new { description = "Redis replacement", tags = Array.Empty<string>() }),
            File("RedisService", name, new
            {
                resourceGroup = group,
                placement = new { deploymentTarget = target, computeProfile = new { name = profile, revision = 1 } },
                storage = new { sizeGb = 1, profile = new { name = profile, revision = 1 } },
                tags = Array.Empty<string>()
            })
        ];
    }
}
