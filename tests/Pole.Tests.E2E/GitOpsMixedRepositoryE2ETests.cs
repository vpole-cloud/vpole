using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.E2E;

public sealed partial class GitOpsRepositoryFlowE2ETests
{
    private const string MixedGroup = "gitops-mixed-group";
    private const string MixedScope = "gitops-mixed-scope";
    private const string MixedSecret = "gitops-mixed-auth";
    private const string MixedProfile = "gitops-mixed-profile";

    [Fact]
    public async Task MixedRepositoryWaitsForSecretsRepairsDriftAndThenPerformsNoOp()
    {
        using var evidence = new TestPhaseJournal(nameof(MixedRepositoryWaitsForSecretsRepairsDriftAndThenPerformsNoOp))
            .Activate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var ct = timeout.Token;
        fixture.EnableGitOpsNamespaceCleanup();
        var image = await fixture.CreateGitOpsFunctionImageAsync(ct);
        var target = await CreateMixedCatalogAsync(ct);
        var connection = await ConnectAsync(ct);
        var commit = new string('d', 40);
        var files = MixedManifests(target, image);
        using (var published = await fixture.Stub.PostAsJsonAsync("/__e2e/gitops-mixed", new { commit, files }, ct))
            Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);

        var created = await JsonAsync(HttpMethod.Post, Root + "/", new
        {
            idempotencyKey = Guid.NewGuid(),
            provider = "GitHub",
            connectionId = connection,
            externalRepositoryId = "9003",
            branch = "main",
            manifestDirectory = "manifests",
            pollIntervalSeconds = 60
        }, ct);
        var source = created.GetProperty("id").GetString()!;
        var blocked = await WaitMixedAsync(source, item => item.GetProperty("status").GetString() == "Blocked", ct);
        var blockedRun = LatestRunId(blocked);
        var operations = await OperationsAsync(source, blockedRun, ct);
        var ids = operations.GetProperty("items").EnumerateArray()
            .GroupBy(item => item.GetProperty("kind").GetString()!)
            .ToDictionary(group => group.Key, group => group.First().GetProperty("resourceId").GetString()!);
        Assert.Equal(8, ids.Count);
        Assert.Null(blocked.GetProperty("synchronization").GetProperty("fullyAppliedCommit").GetString());
        await fixture.AssertGitOpsPreparedWorkloadsAreInertAsync(ids, ct);

        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var secret = await JsonAsync(HttpMethod.Get, $"/api/v1/secrets/{ids["Secret"]}", null, ct);
        await JsonAsync(HttpMethod.Post, $"/api/v1/secrets/{ids["Secret"]}/versions", new
        {
            expectedVersion = secret.GetProperty("currentVersion").GetInt64(),
            values = new Dictionary<string, string> { ["password"] = value }
        }, ct);
        var applied = await AppliedAsync(source, commit, ct);
        Assert.Equal(10, applied.GetProperty("inventory").GetProperty("owned").GetInt64());
        Assert.NotEqual(blockedRun, LatestRunId(applied));
        await fixture.WaitForNamespaceCreationAsync(ids["ResourceGroup"], ct);
        await fixture.VerifyGitOpsPostgresBootstrapAsync(ids["PostgresCluster"], value, ct);

        // A controlled database fixture is the only writer bypassing the production ownership guards.
        await fixture.InjectMixedGitOpsDriftAsync(ids, ct);
        var repaired = await WaitMixedAsync(source, item =>
            LatestRunId(item) != LatestRunId(applied) &&
            item.GetProperty("status").GetString() == "ConfigurationApplied" &&
            item.GetProperty("latestRun").GetProperty("operations").GetProperty("update").GetInt32() >= 3, ct);
        Assert.Equal(commit, repaired.GetProperty("synchronization").GetProperty("fullyAppliedCommit").GetString());
        operations = await OperationsAsync(source, LatestRunId(repaired), ct);
        Assert.All(operations.GetProperty("items").EnumerateArray(), operation =>
            Assert.Equal("Succeeded", operation.GetProperty("status").GetString()));
        Assert.False(operations.GetRawText().Contains(value, StringComparison.Ordinal), "API leaked Secret content.");
        var stable = await fixture.ReadMixedGitOpsBusinessVersionsAsync(ids, ct);

        await JsonAsync(HttpMethod.Post, $"{Root}/{source}/sync", new
        {
            expectedRevision = 1,
            idempotencyKey = Guid.NewGuid()
        }, ct);
        var unchanged = await WaitMixedAsync(source, item => LatestRunId(item) != LatestRunId(repaired) &&
            item.GetProperty("status").GetString() == "AlreadyCurrent", ct);
        var summary = unchanged.GetProperty("latestRun").GetProperty("operations");
        Assert.Equal(14, summary.GetProperty("noOp").GetInt32());
        Assert.Equal(0, summary.GetProperty("update").GetInt32());
        Assert.Equal(0, summary.GetProperty("create").GetInt32());
        Assert.Equal(stable, await fixture.ReadMixedGitOpsBusinessVersionsAsync(ids, ct));

        using var foreign = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"{Root}/{source}",
            fixture.Beta, fixture.Beta.DefaultTenantId);
        using var denied = await fixture.Api.SendAsync(foreign, ct);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        var journal = (await fixture.Stub.GetFromJsonAsync<JsonElement[]>("/__e2e/requests", ct))!;
        Assert.DoesNotContain(journal, item => item.GetProperty("path").GetString()!
            .StartsWith("/repos/pole-e2e/gitops-mixed/contents/", StringComparison.Ordinal));
    }

    private Task<JsonElement> WaitMixedAsync(string source, Func<JsonElement, bool> ready, CancellationToken ct) =>
        WaitAsync(source, item =>
        {
            Assert.NotEqual("Failed", item.GetProperty("status").GetString());
            return ready(item);
        }, ct);

    private static string LatestRunId(JsonElement source) =>
        source.GetProperty("synchronization").GetProperty("latestRunId").GetString()!;

    private Task<JsonElement> OperationsAsync(string source, string run, CancellationToken ct) =>
        JsonAsync(HttpMethod.Get, $"{Root}/{source}/runs/{run}/operations?pageSize=50", null, ct);

    private async Task<string> CreateMixedCatalogAsync(CancellationToken ct)
    {
        var targets = await AdminJsonAsync(HttpMethod.Get, "/api/v1/platform/deployment-targets", null, ct);
        var target = targets.EnumerateArray().Single(item => item.GetProperty("isDefault").GetBoolean());
        var targetId = target.GetProperty("id").GetString()!;
        var configuration = target.GetProperty("workloadConfiguration");
        var entries = configuration.GetProperty("networkSources").GetProperty("entries")
            .EnumerateObject().ToDictionary(item => item.Name, item => (object)item.Value.Clone());
        entries["functions/gateway"] = new { kind = "Cidr", cidr = "127.0.0.1/32" };
        await AdminJsonAsync(HttpMethod.Put, $"/api/v1/platform/deployment-targets/{targetId}", new
        {
            displayName = target.GetProperty("displayName").GetString(),
            enabled = true,
            acceptsWorkloads = true,
            revision = target.GetProperty("revision").GetInt64(),
            workloadConfiguration = new
            {
                ingress = configuration.GetProperty("ingress"),
                networkSources = new { entries }
            }
        }, ct);
        await AdminJsonAsync(HttpMethod.Post, "/api/v1/platform/compute-profiles/", new
        {
            technicalName = MixedProfile,
            displayName = "Mixed GitOps",
            description = "Stage five E2E",
            resources = new
            {
                requests = new { cpuMillis = 100, memoryMi = 256 },
                limits = new { cpuMillis = 500, memoryMi = 512 }
            },
            architecture = "Any",
            extendedResources = Array.Empty<object>(),
            allowedResourceTypes = new[] { "pole/web-service", "pole/redis", "pole/postgresql", "pole/function-app" },
            tenantAccess = new { kind = "all-tenants", tenantIds = Array.Empty<string>() },
            targetMappings = new[] { new { deploymentTargetId = targetId, nodeSelector = Array.Empty<object>(),
                requiredNodeAffinity = Array.Empty<object>(), preferredNodeAffinity = Array.Empty<object>(),
                tolerations = Array.Empty<object>(), runtimeClassName = (string?)null } },
            isEnabled = true,
            isDefault = false
        }, ct);
        var storage = await AdminJsonAsync(HttpMethod.Post, "/api/v1/platform/storage/profiles", new
        {
            technicalName = MixedProfile,
            displayName = "Mixed GitOps",
            description = "Stage five E2E",
            mediaType = "Network",
            performanceTier = "Standard",
            isEnabled = true,
            isDefault = false,
            targetMappings = new[] { new { deploymentTargetId = targetId,
                storageClassName = fixture.LabState.GetRequiredTarget("default").StorageClassName } },
            allowedResourceTypes = new[] { "pole/redis", "pole/postgresql" },
            tenantAccess = new { mode = "SelectedTenants", tenantIds = new[] { fixture.Alpha.DefaultTenantId } }
        }, ct);
        Assert.True(storage.GetProperty("isEnabled").GetBoolean());
        return target.GetProperty("technicalName").GetString()!;
    }

    private async Task<JsonElement> AdminJsonAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = fixture.CreatePlatformAdminRequest(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);

        using var response = await fixture.Api.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static object[] MixedManifests(string target, FunctionSdkTestImage image)
    {
        var placement = new
        {
            deploymentTarget = target,
            isolationScope = MixedScope,
            computeProfile = new { name = MixedProfile, revision = 1 }
        };
        var storage = new { sizeGb = 1, profile = new { name = MixedProfile, revision = 1 } };
        var secretEnvironment = new Dictionary<string, object>
        {
            ["STAGE_FIVE_SECRET"] = new { secret = MixedSecret, key = "password" }
        };
        var probe = new
        {
            path = "/",
            port = "80",
            initialDelaySeconds = 0,
            periodSeconds = 2,
            timeoutSeconds = 1,
            failureThreshold = 3,
            successThreshold = 1
        };
        object File(string kind, string name, object spec) => new
        {
            path = $"manifests/{kind}-{name}.yaml",
            content = JsonSerializer.Serialize(new
            {
                apiVersion = "pole.io/v1alpha1",
                kind,
                metadata = new { name },
                spec
            })
        };
        object Binding(string name, string kind, string consumer, string usage) => File("SecretBinding", name,
            new { secret = MixedSecret, consumer = new { kind, name = consumer }, usage });
        return
        [
            File("ResourceGroup", MixedGroup, new { description = "Mixed repository", tags = new[] { "gitops" } }),
            File("IsolationScope", MixedScope, new { resourceGroup = MixedGroup }),
            File("Secret", MixedSecret, new { resourceGroup = MixedGroup, isolationScope = MixedScope,
                keys = new[] { "password" } }),
            File("RedisService", "mixed-redis", new { resourceGroup = MixedGroup, placement, storage,
                tags = new[] { "gitops" } }),
            File("PostgresCluster", "mixed-postgres", new { resourceGroup = MixedGroup, placement, storage,
                version = 16, instances = 1, username = "app",
                bootstrapPassword = new { secret = MixedSecret, key = "password" } }),
            File("WebService", "mixed-web", new { resourceGroup = MixedGroup, placement,
                image = new { server = "docker.io", name = "traefik/whoami", tag = "v1.11.0" },
                ports = new[] { new { name = "http", servicePort = 80, targetPort = "80", protocol = "TCP" } },
                probes = new { startup = probe, readiness = probe, liveness = probe },
                secretEnvironment, tags = new[] { "gitops" } }),
            File("FunctionApp", "mixed-function", new { resourceGroup = MixedGroup, placement,
                functionRoute = "mixed-function", image = new { server = image.RegistryHost,
                    name = image.Repository, tag = image.Tag },
                environment = new Dictionary<string, string>(), secretEnvironment,
                resources = new { requests = new { cpuMillis = 100, memoryMi = 256 },
                    limits = new { cpuMillis = 500, memoryMi = 512 } },
                scaling = new { minInstances = 1, maxInstances = 1, targetConcurrencyPerInstance = 1,
                    maxConcurrencyPerInstance = 10, scaleDownDelaySeconds = 60, activationTimeoutSeconds = 120 },
                handlers = new[] { new { name = "hello", maxConcurrencyPerInstance = 10,
                    trigger = new { type = "http", route = "hello", methods = new[] { "GET" },
                        exposure = "Internal" } } },
                tags = new[] { "gitops" } }),
            Binding("mixed-web-binding", "WebService", "mixed-web", "Runtime"),
            Binding("mixed-function-binding", "FunctionApp", "mixed-function", "Runtime"),
            Binding("mixed-bootstrap-grant", "PostgresCluster", "mixed-postgres", "Bootstrap")
        ];
    }
}
