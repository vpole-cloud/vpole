using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed partial class GitOpsRepositoryFlowE2ETests
{
    private const string MixedGroup = "gitops-mixed-group";
    private const string MixedScope = "gitops-mixed-scope";
    private const string MixedSecret = "gitops-mixed-auth";
    private const string MixedProfile = "gitops-mixed-profile";

    [SourceBuildFact]
    public async Task MixedRepositoryWaitsForSecretsRepairsDriftAndThenPerformsNoOp()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var evidence = new TestPhaseJournal(nameof(MixedRepositoryWaitsForSecretsRepairsDriftAndThenPerformsNoOp))
            .Activate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        var ct = timeout.Token;
        fixture.EnableGitOpsNamespaceCleanup();
        using (_timings.Measure("e2e.source-build.infrastructure.ready"))
        {
            await fixture.EnsureSourceBuildInfrastructureReadyAsync(ct);
        }

        using (var revision = await fixture.Stub.PostAsJsonAsync("/__e2e/revision", new { revision = "v1" }, ct))
        {
            revision.EnsureSuccessStatusCode();
        }

        FunctionSdkTestImage image;
        using (_timings.Measure("e2e.gitops.image.prepare"))
        {
            image = await fixture.CreateGitOpsFunctionImageAsync(ct);
        }

        var target = await CreateMixedCatalogAsync(ct);
        var connection = await ConnectAsync(ct);
        var commit = new string('d', 40);
        var files = MixedManifests(target, image);
        using (var published = await fixture.Stub.PostAsJsonAsync("/__e2e/gitops-mixed", new { commit, files }, ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);
        }

        var created = await JsonAsync(
            HttpMethod.Post,
            Root + "/",
            new
            {
                idempotencyKey = Guid.NewGuid(),
                provider = "GitHub",
                connectionId = connection,
                externalRepositoryId = "9003",
                branch = "main",
                manifestDirectory = "manifests",
                pollIntervalSeconds = 60,
            },
            ct);
        var source = created.GetProperty("id").GetString()!;
        var blocked = await WaitMixedAsync(source, item => item.GetProperty("status").GetString() == "Blocked", ct);
        var blockedRun = LatestRunId(blocked);
        var operations = await OperationsAsync(source, blockedRun, ct);
        var ids = operations.GetProperty("items")
            .EnumerateArray()
            .GroupBy(item => item.GetProperty("kind").GetString()!)
            .ToDictionary(group => group.Key, group => group.First().GetProperty("resourceId").GetString()!);
        Assert.Equal(13, ids.Count);
        Assert.Null(blocked.GetProperty("synchronization").GetProperty("fullyAppliedCommit").GetString());
        await fixture.AssertGitOpsPreparedWorkloadsAreInertAsync(ids, ct);

        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var secret = await JsonAsync(HttpMethod.Get, $"/api/v1/secrets/{ids["Secret"]}", null, ct);
        await JsonAsync(
            HttpMethod.Post,
            $"/api/v1/secrets/{ids["Secret"]}/versions",
            new
            {
                expectedVersion = secret.GetProperty("currentVersion").GetInt64(),
                values = new Dictionary<string, string> { ["password"] = value },
            },
            ct);
        var applied = await AppliedAsync(source, commit, ct);
        Assert.Equal(15, applied.GetProperty("inventory").GetProperty("owned").GetInt64());
        Assert.NotEqual(blockedRun, LatestRunId(applied));
        using (_timings.Measure("e2e.gitops.namespace.ready"))
        {
            await fixture.WaitForNamespaceCreationAsync(ids["ResourceGroup"], ct);
        }

        var webService = await JsonAsync(HttpMethod.Get, $"/api/v1/web-services/{ids["WebService"]}", null, ct);
        Assert.Equal("AwaitingFirstArtifact", webService.GetProperty("lifecycle").GetString());
        Assert.Equal(JsonValueKind.Null, webService.GetProperty("spec").GetProperty("image").ValueKind);
        await fixture.AssertGitOpsAwaitingArtifactIsInertAsync(ids["WebService"], ct);
        Assert.Empty((await MixedBuildsAsync(ids["WebService"], ct)).EnumerateArray());
        var releases = await JsonAsync(HttpMethod.Get, $"/api/v1/web-services/{ids["WebService"]}/releases", null, ct);
        Assert.Empty(releases.GetProperty("releases").GetProperty("items").EnumerateArray());

        // Inertness was verified above. SQL bootstrap and first artifact delivery are independent.
        async Task VerifyPostgresAsync()
        {
            using (_timings.Measure("e2e.gitops.postgres.ready"))
            {
                await fixture.VerifyGitOpsPostgresBootstrapAsync(ids["PostgresCluster"], value, ct);
            }
        }

        async Task<JsonElement> BuildFirstArtifactAsync()
        {
            var build = await JsonAsync(
                HttpMethod.Post,
                $"/api/v1/source-build-configurations/{ids["SourceBuildConfiguration"]}/builds",
                null,
                ct);
            return await WaitMixedBuildAsync(build.GetProperty("id").GetString()!, ct);
        }

        var postgresBootstrap = VerifyPostgresAsync();
        var artifactBuild = BuildFirstArtifactAsync();
        await Task.WhenAll(postgresBootstrap, artifactBuild);
        var completed = await artifactBuild;
        var buildId = completed.GetProperty("id").GetString()!;
        Assert.Equal(new string('1', 40), completed.GetProperty("commitSha").GetString());
        Assert.NotEqual(commit, completed.GetProperty("commitSha").GetString());
        var digest = completed.GetProperty("state").GetProperty("imageDigest").GetString()!;
        Assert.Equal(
            digest,
            await fixture.GetHarborArtifactDigestAsync(fixture.Alpha.DefaultTenantId, ids["WebService"], digest, ct));
        using (_timings.Measure("e2e.gitops.web-service.ready"))
        {
            await fixture.WaitForWebServiceReadyAsync(ids["ResourceGroup"], "mixed-web", ct);
        }

        await fixture.AssertGitOpsDeliveryWiringAsync(ids, digest, ct);

        // Pause fences an in-flight poll before the fixture changes all three resources.
        // Otherwise a run can read PostgreSQL before the drift and the other resources after it.
        var beforeDrift = await JsonAsync(HttpMethod.Get, $"{Root}/{source}", null, ct);
        var pausedForDrift = await LifecycleActivityAsync(source, "pause", beforeDrift, ct);
        Assert.Equal("Paused", pausedForDrift.GetProperty("activity").GetString());

        // A controlled database fixture is the only writer bypassing the production ownership guards.
        await fixture.InjectMixedGitOpsDriftAsync(ids, ct);
        await LifecycleActivityAsync(source, "resume", pausedForDrift, ct);
        var repaired = await WaitMixedAsync(
            source,
            item => LatestRunId(item) != LatestRunId(applied) &&
                item.GetProperty("status").GetString() == "ConfigurationApplied" && item.GetProperty("latestRun")
                    .GetProperty("operations")
                    .GetProperty("update")
                    .GetInt32() >= 3,
            ct);
        Assert.Equal(commit, repaired.GetProperty("synchronization").GetProperty("fullyAppliedCommit").GetString());
        operations = await OperationsAsync(source, LatestRunId(repaired), ct);
        Assert.All(
            operations.GetProperty("items").EnumerateArray(),
            operation => Assert.Equal("Succeeded", operation.GetProperty("status").GetString()));
        Assert.False(operations.GetRawText().Contains(value, StringComparison.Ordinal), "API leaked Secret content.");
        await fixture.RemoveGitOpsHostnameAttachmentAsync(ids["HostnameBinding"], ct);
        var attachmentRepair = await WaitMixedAsync(
            source,
            item => LatestRunId(item) != LatestRunId(repaired) &&
                item.GetProperty("status").GetString() == "ConfigurationApplied",
            ct);
        var attachmentOperations = await OperationsAsync(source, LatestRunId(attachmentRepair), ct);
        Assert.Contains(
            attachmentOperations.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == "HostnameBinding" &&
                item.GetProperty("plan").GetProperty("action").GetString() == "Update" &&
                item.GetProperty("status").GetString() == "Succeeded");
        await fixture.AssertGitOpsDeliveryWiringAsync(ids, digest, ct);
        var stable = await fixture.ReadMixedGitOpsBusinessVersionsAsync(ids, ct);

        await SyncLifecycleAsync(source, ct);
        var unchanged = await WaitMixedAsync(
            source,
            item => LatestRunId(item) != LatestRunId(attachmentRepair) &&
                item.GetProperty("status").GetString() == "AlreadyCurrent",
            ct);
        var summary = unchanged.GetProperty("latestRun").GetProperty("operations");
        Assert.Equal(19, summary.GetProperty("noOp").GetInt32());
        Assert.Equal(0, summary.GetProperty("update").GetInt32());
        Assert.Equal(0, summary.GetProperty("create").GetInt32());
        Assert.Equal(stable, await fixture.ReadMixedGitOpsBusinessVersionsAsync(ids, ct));

        webService = await JsonAsync(HttpMethod.Get, $"/api/v1/web-services/{ids["WebService"]}", null, ct);
        var authority = webService.GetProperty("spec").GetProperty("imageAuthority");
        Assert.Equal(digest, authority.GetProperty("digest").GetString());
        Assert.Equal(buildId, authority.GetProperty("jobId").GetString());
        Assert.Single((await MixedBuildsAsync(ids["WebService"], ct)).EnumerateArray());
        await fixture.AssertGitOpsDeliveryWiringAsync(ids, digest, ct);

        using var foreign = fixture.CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"{Root}/{source}",
            fixture.Beta,
            fixture.Beta.DefaultTenantId);
        using var denied = await fixture.Api.SendAsync(foreign, ct);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        var journal = (await fixture.Stub.GetFromJsonAsync<JsonElement[]>("/__e2e/requests", ct))!;
        Assert.DoesNotContain(
            journal,
            item => item.GetProperty("path").GetString()!.StartsWith(
                "/repos/pole-e2e/gitops-mixed/contents/",
                StringComparison.Ordinal));

        var native = await fixture.CaptureGitOpsCleanupAsync(ids, ct);
        await LifecycleActivityAsync(source, "pause", unchanged, ct);
        await JsonAsync(
            HttpMethod.Put,
            $"{Root}/{source}",
            new
            {
                expectedRevision = await RevisionOfAsync(source, ct),
                pollIntervalSeconds = 60,
                prune = "Prune",
            },
            ct);
        var emptyCommit = new string('e', 40);
        using (var published = await fixture.Stub.PostAsJsonAsync(
                   "/__e2e/gitops-mixed",
                   new
                   {
                       commit = emptyCommit,
                       files = new[] { new { path = "manifests/.keep", content = "" } },
                   },
                   ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);
        }

        var paused = await JsonAsync(HttpMethod.Get, $"{Root}/{source}", null, ct);
        await LifecycleActivityAsync(source, "resume", paused, ct);
        var requiresApproval = await WaitMixedAsync(
            source,
            item =>
            {
                if (item.GetProperty("status").GetString() == "Blocked")
                {
                    var codes = item.GetProperty("latestRun")
                        .GetProperty("diagnostics")
                        .EnumerateArray()
                        .Select(diagnostic => diagnostic.GetProperty("code").GetString());
                    Assert.Fail("Protected prune preview stopped: " + string.Join(", ", codes));
                }

                return item.GetProperty("status").GetString() == "AwaitingConfirmation";
            },
            ct);
        Assert.Equal(15, requiresApproval.GetProperty("inventory").GetProperty("owned").GetInt64());
        await LifecycleActivityAsync(source, "pause", requiresApproval, ct);
        var deletion = await LifecyclePreviewAsync(
            source,
            new
            {
                intent = "ResourceChanges",
                expectedRevision = await RevisionOfAsync(source, ct),
            },
            ct);
        Assert.Equal(15, deletion.GetProperty("actions").GetProperty("total").GetInt32());
        Assert.All(
            deletion.GetProperty("actions").GetProperty("items").EnumerateArray(),
            action => Assert.Equal("Remove", action.GetProperty("action").GetString()));
        Assert.False(deletion.GetRawText().Contains(value, StringComparison.Ordinal), "Preview leaked Secret content.");
        await ConfirmLifecycleAsync(source, deletion, ct);
        var cleaned = await WaitAsync(
            source,
            item => item.GetProperty("status").GetString() is "Blocked" or "Failed" ||
                (item.GetProperty("status").GetString() == "ConfigurationApplied" &&
                    item.GetProperty("inventory").GetProperty("owned").GetInt64() == 0),
            ct,
            "e2e.gitops.lifecycle.cleanup");
        if (cleaned.GetProperty("status").GetString() is "Blocked" or "Failed")
        {
            var codes = cleaned.GetProperty("latestRun")
                .GetProperty("diagnostics")
                .EnumerateArray()
                .Select(diagnostic => diagnostic.GetProperty("code").GetString());
            var metadata = await fixture.WriteGitOpsLifecycleFailureMetadataAsync(source, LatestRunId(cleaned), ct);
            Assert.Fail("Reviewed cleanup stopped: " + string.Join(", ", codes) + "; metadata=" + metadata);
        }

        Assert.Equal(emptyCommit, cleaned.GetProperty("synchronization").GetProperty("fullyAppliedCommit").GetString());
        using (_timings.Measure("e2e.gitops.native.cleanup"))
        {
            await fixture.AssertGitOpsCleanupCompletedAsync(native, ids, ct);
        }
    }

    private async Task<JsonElement> MixedBuildsAsync(string webServiceId, CancellationToken ct) => (await JsonAsync(
        HttpMethod.Get,
        $"/api/v1/source-build-jobs?webServiceId={webServiceId}",
        null,
        ct)).GetProperty("items");

    private async Task<JsonElement> WaitMixedBuildAsync(string id, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.gitops.build.ready");
        while (true)
        {
            var job = await JsonAsync(HttpMethod.Get, $"/api/v1/source-build-jobs/{id}", null, ct);
            var state = job.GetProperty("state");
            var status = state.GetProperty("status").GetString();
            Assert.False(
                status == "failed",
                "The mixed repository's manual build failed; inspect sanitized job status.");
            if (status == "succeeded")
                return job;

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private Task<JsonElement> WaitMixedAsync(
        string source,
        Func<JsonElement, bool> ready,
        CancellationToken ct) => WaitAsync(
        source,
        item =>
        {
            Assert.NotEqual("Failed", item.GetProperty("status").GetString());
            return ready(item);
        },
        ct);

    private static string LatestRunId(JsonElement source) =>
        source.GetProperty("synchronization").GetProperty("latestRunId").GetString()!;

    private Task<JsonElement> OperationsAsync(
        string source,
        string run,
        CancellationToken ct) => JsonAsync(
        HttpMethod.Get,
        $"{Root}/{source}/runs/{run}/operations?pageSize=50",
        null,
        ct);

    private async Task<string> CreateMixedCatalogAsync(CancellationToken ct, string profileName = MixedProfile)
    {
        var targets = await AdminJsonAsync(HttpMethod.Get, "/api/v1/platform/deployment-targets", null, ct);
        var target = targets.EnumerateArray().Single(item => item.GetProperty("isDefault").GetBoolean());
        var targetId = target.GetProperty("id").GetString()!;
        var configuration = target.GetProperty("workloadConfiguration");
        var entries = configuration.GetProperty("networkSources")
            .GetProperty("entries")
            .EnumerateObject()
            .ToDictionary(item => item.Name, item => (object)item.Value.Clone());
        entries["functions/gateway"] = new { kind = "Cidr", cidr = "127.0.0.1/32" };
        await AdminJsonAsync(
            HttpMethod.Put,
            $"/api/v1/platform/deployment-targets/{targetId}",
            new
            {
                displayName = target.GetProperty("displayName").GetString(),
                enabled = true,
                acceptsWorkloads = true,
                revision = target.GetProperty("revision").GetInt64(),
                workloadConfiguration = new
                {
                    ingress = configuration.GetProperty("ingress"),
                    networkSources = new { entries },
                },
            },
            ct);
        await AdminJsonAsync(
            HttpMethod.Post,
            "/api/v1/platform/compute-profiles/",
            new
            {
                technicalName = profileName,
                displayName = "Mixed GitOps",
                description = "Stage six E2E",
                resources = new
                {
                    requests = new { cpuMillis = 100, memoryMi = 256 },
                    limits = new { cpuMillis = 500, memoryMi = 512 },
                },
                architecture = "Any",
                extendedResources = Array.Empty<object>(),
                allowedResourceTypes = new[]
                    { "pole/web-service", "pole/redis", "pole/postgresql", "pole/function-app" },
                tenantAccess = new { kind = "all-tenants", tenantIds = Array.Empty<string>() },
                targetMappings = new[]
                {
                    new
                    {
                        deploymentTargetId = targetId, nodeSelector = Array.Empty<object>(),
                        requiredNodeAffinity = Array.Empty<object>(), preferredNodeAffinity = Array.Empty<object>(),
                        tolerations = Array.Empty<object>(), runtimeClassName = (string?)null,
                    },
                },
                isEnabled = true,
                isDefault = false,
            },
            ct);
        var storage = await AdminJsonAsync(
            HttpMethod.Post,
            "/api/v1/platform/storage/profiles",
            new
            {
                technicalName = profileName,
                displayName = "Mixed GitOps",
                description = "Stage six E2E",
                mediaType = "Network",
                performanceTier = "Standard",
                isEnabled = true,
                isDefault = false,
                targetMappings = new[]
                {
                    new
                    {
                        deploymentTargetId = targetId,
                        storageClassName = fixture.LabState.GetRequiredTarget("default").StorageClassName,
                    },
                },
                allowedResourceTypes = new[] { "pole/redis", "pole/postgresql" },
                tenantAccess = new { mode = "SelectedTenants", tenantIds = new[] { fixture.Alpha.DefaultTenantId } },
            },
            ct);
        Assert.True(storage.GetProperty("isEnabled").GetBoolean());
        return target.GetProperty("technicalName").GetString()!;
    }

    private async Task<JsonElement> AdminJsonAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct)
    {
        using var request = fixture.CreatePlatformAdminRequest(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);

        using var response = await fixture.Api.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static IReadOnlyList<RepositoryExampleFile> MixedManifests(string target, FunctionSdkTestImage image) =>
        GitOpsRepositoryExamples.Full(
            new GitOpsExampleEnvironment(
                target,
                MixedProfile,
                "github-e2e",
                "9001",
                "gitops-application",
                new GitOpsExampleImage(image.RegistryHost, image.Repository, image.Tag)));
}
