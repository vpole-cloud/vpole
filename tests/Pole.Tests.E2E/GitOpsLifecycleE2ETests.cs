using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.E2E;

public sealed partial class GitOpsRepositoryFlowE2ETests
{
    [Fact]
    public async Task LifecycleRequiresFreshApprovalAndReturnsManualControlAfterDetach()
    {
        using var evidence = new TestPhaseJournal(
            nameof(LifecycleRequiresFreshApprovalAndReturnsManualControlAfterDetach)).Activate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var ct = timeout.Token;
        fixture.EnableGitOpsNamespaceCleanup();
        var connection = await ConnectAsync(ct);
        const string groupName = "lifecycle-adopted";
        const string renamed = "lifecycle-renamed";
        var manual = await JsonAsync(HttpMethod.Post, "/api/v1/resource-groups/", new
        {
            name = groupName,
            description = "Manual group",
            tags = Array.Empty<string>()
        }, ct);
        var originalId = manual.GetProperty("id").GetString()!;
        await PublishLifecycleAsync('5', LifecycleFiles(groupName, includeSecret: true), ct);
        var source = await CreateLifecycleSourceAsync(connection, "alpha", ct);
        var other = await CreateLifecycleSourceAsync(connection, "beta", ct);
        await AppliedAsync(other, new string('5', 40), ct);
        var awaiting = await WaitAsync(source,
            item => item.GetProperty("status").GetString() == "Blocked", ct);
        await LifecycleActivityAsync(source, "pause", awaiting, ct);
        var preview = await LifecyclePreviewAsync(source, new
        {
            intent = "Adoption",
            expectedRevision = await RevisionOfAsync(source, ct),
            resources = new[] { new { kind = "ResourceGroup", name = groupName, resourceId = originalId } }
        }, ct);
        await ConfirmLifecycleAsync(source, preview, ct);
        var adopted = await LifecycleAppliedAsync(source, new string('5', 40), ct);
        Assert.Equal("Paused", adopted.GetProperty("activity").GetString());
        Assert.Equal(2, adopted.GetProperty("inventory").GetProperty("owned").GetInt64());
        var operations = await OperationsAsync(source, LatestRunId(adopted), ct);
        Assert.Contains(operations.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("resourceId").GetString() == originalId);
        await ExpectLifecycleStatusAsync(HttpMethod.Put, $"/api/v1/resource-groups/{originalId}", new
        {
            description = "Forbidden while Git owned",
            tags = Array.Empty<string>()
        }, HttpStatusCode.Conflict, ct);
        await ExpectLifecycleStatusAsync(HttpMethod.Post, $"{Root}/{source}/sync", new
        {
            expectedRevision = adopted.GetProperty("revision").GetInt64(),
            idempotencyKey = Guid.NewGuid()
        }, HttpStatusCode.Conflict, ct);

        // A paused source may execute explicit reviewed work, but ordinary Sync waits for Resume.
        await LifecycleActivityAsync(source, "resume", adopted, ct);
        var resumed = await WaitAsync(source, item => LatestRunId(item) != LatestRunId(adopted) &&
            item.GetProperty("status").GetString() == "AlreadyCurrent", ct);
        await JsonAsync(HttpMethod.Put, $"{Root}/{source}", new
        {
            expectedRevision = resumed.GetProperty("revision").GetInt64(),
            pollIntervalSeconds = 300,
            prune = "Prune"
        }, ct);
        await PublishLifecycleAsync('6', LifecycleFiles(groupName, includeSecret: false), ct);
        await SyncLifecycleAsync(source, ct);
        var protectedRemoval = await WaitAsync(source,
            item => item.GetProperty("status").GetString() == "AwaitingConfirmation", ct);
        Assert.Equal(2, protectedRemoval.GetProperty("inventory").GetProperty("owned").GetInt64());
        var stale = await LifecyclePreviewAsync(source, new
        {
            intent = "ResourceChanges",
            expectedRevision = protectedRemoval.GetProperty("revision").GetInt64()
        }, ct);
        Assert.Contains(stale.GetProperty("actions").GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("resource").GetProperty("kind").GetString() == "Secret" &&
            item.GetProperty("action").GetString() == "Remove");
        await LifecycleActivityAsync(source, "pause", protectedRemoval, ct);
        await ExpectLifecycleStatusAsync(HttpMethod.Post, PreviewPath(source, stale) + "/confirm",
            new { expectedRevision = stale.GetProperty("revision").GetInt64() }, HttpStatusCode.Conflict, ct);
        preview = await LifecyclePreviewAsync(source, new
        {
            intent = "ResourceChanges",
            expectedRevision = await RevisionOfAsync(source, ct)
        }, ct);
        await ConfirmLifecycleAsync(source, preview, ct);
        var removed = await LifecycleAppliedAsync(source, new string('6', 40), ct);
        Assert.Equal(1, removed.GetProperty("inventory").GetProperty("owned").GetInt64());

        await PublishLifecycleAsync('7', LifecycleFiles(renamed, includeSecret: false), ct);
        preview = await LifecyclePreviewAsync(source, new
        {
            intent = "Replacement",
            expectedRevision = await RevisionOfAsync(source, ct),
            replacements = new[]
            {
                new { original = new { kind = "ResourceGroup", name = groupName, resourceId = originalId },
                    desired = new { kind = "ResourceGroup", name = renamed } }
            }
        }, ct);
        var replacement = Assert.Single(preview.GetProperty("actions").GetProperty("items").EnumerateArray(),
            item => item.GetProperty("action").GetString() == "Replace");
        var replacementId = replacement.GetProperty("replacement").GetProperty("resourceId").GetString()!;
        Assert.NotEqual(originalId, replacementId);
        await ConfirmLifecycleAsync(source, preview, ct);
        await LifecycleAppliedAsync(source, new string('7', 40), ct);
        await ExpectLifecycleStatusAsync(HttpMethod.Get, $"/api/v1/resource-groups/{originalId}", null,
            HttpStatusCode.NotFound, ct);

        // Relocation changes the repository directory, retaining accepted identities at the same addresses.
        await PublishLifecycleAsync('8', LifecycleFiles(renamed, includeSecret: false, includeDestination: true), ct);
        preview = await LifecyclePreviewAsync(source, new
        {
            intent = "Relocation",
            expectedRevision = await RevisionOfAsync(source, ct),
            destination = new
            {
                provider = "GitHub",
                connectionId = connection,
                externalRepositoryId = "9004",
                branch = "main",
                manifestDirectory = "moved"
            }
        }, ct);
        await ConfirmLifecycleAsync(source, preview, ct);
        var relocated = await LifecycleAppliedAsync(source, new string('8', 40), ct);
        Assert.Equal("moved", relocated.GetProperty("repository").GetProperty("manifestDirectory").GetString());
        await JsonAsync(HttpMethod.Get, $"/api/v1/resource-groups/{replacementId}", null, ct);
        await ExpectLifecycleStatusAsync(HttpMethod.Delete,
            $"{Root}/{source}?expectedRevision={relocated.GetProperty("revision").GetInt64()}", null,
            HttpStatusCode.Conflict, ct);

        preview = await LifecyclePreviewAsync(source, new
        {
            intent = "Detach",
            expectedRevision = await RevisionOfAsync(source, ct)
        }, ct);
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("commit").ValueKind);
        await ConfirmLifecycleAsync(source, preview, ct);
        var detached = await WaitAsync(source, item => item.GetProperty("activity").GetString() == "Detached", ct);
        Assert.Equal(0, detached.GetProperty("inventory").GetProperty("owned").GetInt64());
        await JsonAsync(HttpMethod.Put, $"/api/v1/resource-groups/{replacementId}", new
        {
            description = "Manual control restored",
            tags = new[] { "detached" }
        }, ct);
        await ExpectLifecycleStatusAsync(HttpMethod.Delete,
            $"{Root}/{source}?expectedRevision={detached.GetProperty("revision").GetInt64()}", null,
            HttpStatusCode.NoContent, ct);
        await ExpectLifecycleStatusAsync(HttpMethod.Get, $"{Root}/{source}", null, HttpStatusCode.NotFound, ct);
        var independent = await JsonAsync(HttpMethod.Get, $"{Root}/{other}", null, ct);
        Assert.Equal("Active", independent.GetProperty("activity").GetString());
        Assert.Equal(1, independent.GetProperty("inventory").GetProperty("owned").GetInt64());
    }

    private Task<JsonElement> LifecycleAppliedAsync(string source, string commit, CancellationToken ct) =>
        WaitAsync(source, item =>
        {
            var status = item.GetProperty("status").GetString();
            if (status is "Blocked" or "Failed")
            {
                var codes = item.GetProperty("latestRun").GetProperty("diagnostics").EnumerateArray()
                    .Select(diagnostic => diagnostic.GetProperty("code").GetString());
                Assert.Fail("Lifecycle run stopped: " + string.Join(", ", codes));
            }
            return status == "ConfigurationApplied" && item.GetProperty("synchronization")
                .GetProperty("fullyAppliedCommit").GetString() == commit;
        }, ct);

    private async Task<string> CreateLifecycleSourceAsync(string connection, string directory, CancellationToken ct)
    {
        var created = await JsonAsync(HttpMethod.Post, Root + "/", new
        {
            idempotencyKey = Guid.NewGuid(),
            provider = "GitHub",
            connectionId = connection,
            externalRepositoryId = "9004",
            branch = "main",
            manifestDirectory = directory,
            pollIntervalSeconds = 300
        }, ct);
        return created.GetProperty("id").GetString()!;
    }

    private async Task PublishLifecycleAsync(char commit, object[] files, CancellationToken ct)
    {
        using var published = await fixture.Stub.PostAsJsonAsync("/__e2e/gitops-lifecycle",
            new { commit = new string(commit, 40), files }, ct);
        Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);
    }

    private static object[] LifecycleFiles(string group, bool includeSecret, bool includeDestination = false)
    {
        object File(string directory, string kind, string name, object spec) => new
        {
            path = $"{directory}/{kind}-{name}.yaml",
            content = JsonSerializer.Serialize(new
            {
                apiVersion = "pole.io/v1alpha1",
                kind,
                metadata = new { name },
                spec
            })
        };
        var files = new List<object>
        {
            File("alpha", "ResourceGroup", group,
                new { description = "Lifecycle group", tags = Array.Empty<string>() }),
            File("beta", "ResourceGroup", "lifecycle-other", new { description = "Independent source" })
        };
        if (includeSecret)
            files.Add(File("alpha", "Secret", "lifecycle-secret",
                new { resourceGroup = group, keys = new[] { "key" } }));
        if (includeDestination)
            files.Add(File("moved", "ResourceGroup", group,
                new { description = "Lifecycle group", tags = Array.Empty<string>() }));
        return files.ToArray();
    }

    private Task<JsonElement> LifecyclePreviewAsync(string source, object body, CancellationToken ct) =>
        JsonAsync(HttpMethod.Post, $"{Root}/{source}/previews", body, ct);

    private async Task ConfirmLifecycleAsync(string source, JsonElement preview, CancellationToken ct) =>
        await JsonAsync(HttpMethod.Post, PreviewPath(source, preview) + "/confirm",
            new { expectedRevision = preview.GetProperty("revision").GetInt64() }, ct);

    private static string PreviewPath(string source, JsonElement preview) =>
        $"{Root}/{source}/previews/{preview.GetProperty("id").GetString()}";

    private async Task<long> RevisionOfAsync(string source, CancellationToken ct) =>
        (await JsonAsync(HttpMethod.Get, $"{Root}/{source}", null, ct)).GetProperty("revision").GetInt64();

    private Task<JsonElement> LifecycleActivityAsync(string source, string action, JsonElement current,
        CancellationToken ct) => JsonAsync(HttpMethod.Post, $"{Root}/{source}/{action}",
        new { expectedRevision = current.GetProperty("revision").GetInt64() }, ct);

    private async Task SyncLifecycleAsync(string source, CancellationToken ct) =>
        await JsonAsync(HttpMethod.Post, $"{Root}/{source}/sync",
            new { expectedRevision = await RevisionOfAsync(source, ct), idempotencyKey = Guid.NewGuid() }, ct);

    private async Task ExpectLifecycleStatusAsync(HttpMethod method, string path, object? body,
        HttpStatusCode expected, CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(method, path, fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.Equal(expected, response.StatusCode);
    }
}
