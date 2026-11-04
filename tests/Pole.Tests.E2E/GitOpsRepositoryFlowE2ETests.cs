using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class GitOpsRepositoryFlowE2ETests(PoleE2EFixture fixture)
{
    private const string Root = "/api/v1/gitops/sources";

    [Fact]
    public async Task RepositorySourceAppliesThroughOutboxWorkerWebhookAndPolling()
    {
        using var evidence = new TestPhaseJournal(nameof(RepositorySourceAppliesThroughOutboxWorkerWebhookAndPolling))
            .Activate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        var ct = timeout.Token;
        var connection = await ConnectAsync(ct);
        await RevisionAsync("v1", ct);
        var created = await JsonAsync(HttpMethod.Post,
            Root + "/",
            new
            {
                idempotencyKey = Guid.NewGuid(),
                provider = "GitHub",
                connectionId = connection,
                externalRepositoryId = "9002",
                branch = "main",
                manifestDirectory = "manifests",
                pollIntervalSeconds = 300
            },
            ct);
        var source = created.GetProperty("id").GetString()!;
        var first = await AppliedAsync(source, new string('a', 40), ct);
        Assert.Equal(1, first.GetProperty("inventory").GetProperty("owned").GetInt64());
        var runId = first.GetProperty("synchronization").GetProperty("latestRunId").GetString();
        var operations = await JsonAsync(HttpMethod.Get, $"{Root}/{source}/runs/{runId}/operations", null, ct);
        var operation = Assert.Single(operations.GetProperty("items").EnumerateArray());
        Assert.Equal("Succeeded", operation.GetProperty("status").GetString());
        var resource = operation.GetProperty("resourceId").GetString()!;
        await JsonAsync(HttpMethod.Get, $"/api/v1/resource-groups/{resource}", null, ct);

        await RevisionAsync("v2", ct);
        await PushAsync(new string('b', 40), ct);
        var second = await AppliedAsync(source, new string('b', 40), ct);
        runId = second.GetProperty("synchronization").GetProperty("latestRunId").GetString();
        var pushed = await JsonAsync(HttpMethod.Get, $"{Root}/{source}/runs/{runId}", null, ct);
        Assert.Equal("Webhook", pushed.GetProperty("trigger").GetString());

        await JsonAsync(HttpMethod.Put, $"{Root}/{source}", new { expectedRevision = 1, pollIntervalSeconds = 60 }, ct);
        await WaitAsync(source,
            item => item.GetProperty("revision").GetInt64() == 2 &&
                    item.GetProperty("synchronization").GetProperty("completedGeneration").GetInt64() ==
                    item.GetProperty("synchronization").GetProperty("requestedGeneration").GetInt64(),
            ct);
        await RevisionAsync("v3", ct); // No webhook: the recurring Mongo scanner must discover this change.
        var polled = await AppliedAsync(source, new string('c', 40), ct);
        runId = polled.GetProperty("synchronization").GetProperty("latestRunId").GetString();
        var pollRun = await JsonAsync(HttpMethod.Get, $"{Root}/{source}/runs/{runId}", null, ct);
        Assert.Equal("Poll", pollRun.GetProperty("trigger").GetString());

        var groupBefore = await JsonAsync(HttpMethod.Get, $"/api/v1/resource-groups/{resource}", null, ct);
        await fixture.InjectResourceGroupDriftAsync(resource, ct);
        var repaired = await WaitAsync(source, item =>
            item.GetProperty("synchronization").GetProperty("latestRunId").GetString() != runId &&
            item.GetProperty("status").GetString() == "ConfigurationApplied" &&
            item.GetProperty("latestRun").GetProperty("operations").GetProperty("update").GetInt32() == 1, ct);
        var repairId = repaired.GetProperty("synchronization").GetProperty("latestRunId").GetString();
        var repairRun = await JsonAsync(HttpMethod.Get, $"{Root}/{source}/runs/{repairId}", null, ct);
        Assert.Equal("Poll", repairRun.GetProperty("trigger").GetString());
        Assert.Equal(new string('c', 40), repaired.GetProperty("synchronization")
            .GetProperty("fullyAppliedCommit").GetString());
        var groupAfter = await JsonAsync(HttpMethod.Get, $"/api/v1/resource-groups/{resource}", null, ct);
        Assert.Equal(groupBefore.GetProperty("description").GetString(),
            groupAfter.GetProperty("description").GetString());
        var repairOperations = await JsonAsync(HttpMethod.Get,
            $"{Root}/{source}/runs/{repairId}/operations", null, ct);
        var repairPlan = Assert.Single(repairOperations.GetProperty("items").EnumerateArray()).GetProperty("plan");
        Assert.Equal("Update", repairPlan.GetProperty("action").GetString());
        Assert.Equal("configuration.description", Assert.Single(repairPlan.GetProperty("changedFields")
            .EnumerateArray()).GetString());

        await JsonAsync(HttpMethod.Post, $"{Root}/{source}/sync",
            new { expectedRevision = 2, idempotencyKey = Guid.NewGuid() }, ct);
        await WaitAsync(source, item =>
            item.GetProperty("synchronization").GetProperty("latestRunId").GetString() != repairId &&
            item.GetProperty("status").GetString() == "AlreadyCurrent" &&
            item.GetProperty("latestRun").GetProperty("operations").GetProperty("noOp").GetInt32() == 1, ct);
        var stable = await JsonAsync(HttpMethod.Get, $"/api/v1/resource-groups/{resource}", null, ct);
        Assert.Equal(groupAfter.GetProperty("revision").GetInt64(), stable.GetProperty("revision").GetInt64());

        await RevisionAsync("deleted", ct);
        await PushAsync(new string('0', 40), ct);
        var failed = await WaitAsync(source, item => item.GetProperty("status").GetString() == "Failed", ct);
        Assert.Equal(new string('c', 40),
            failed.GetProperty("synchronization").GetProperty("fullyAppliedCommit")
                .GetString());
        Assert.Equal(1, failed.GetProperty("inventory").GetProperty("owned").GetInt64());
        Assert.Equal(0, failed.GetProperty("inventory").GetProperty("pendingRemoval").GetInt64());
        var journal = (await fixture.Stub.GetFromJsonAsync<JsonElement[]>("/__e2e/requests", ct))!;
        Assert.Contains(journal,
            item => item.GetProperty("path").GetString() ==
                    $"/repos/pole-e2e/gitops-sample/tarball/{new string('a', 40)}");
        Assert.DoesNotContain(journal,
            item => item.GetProperty("path").GetString()!
                .StartsWith("/repos/pole-e2e/gitops-sample/contents/", StringComparison.Ordinal));
    }

    private async Task<string> ConnectAsync(CancellationToken ct)
    {
        var begin = await JsonAsync(HttpMethod.Post,
            "/api/v1/git-provider-connections/github/install",
            new { technicalName = "github-e2e" },
            ct);
        using var callback = await fixture.Stub.GetAsync(begin.GetProperty("installUrl").GetString(), ct);
        callback.EnsureSuccessStatusCode();
        var connections = await JsonAsync(HttpMethod.Get, "/api/v1/git-provider-connections", null, ct);
        return connections.EnumerateArray().Single(item => item.GetProperty("technicalName").GetString(
            ) == "github-e2e")
            .GetProperty("id").GetString()!;
    }

    private async Task RevisionAsync(string revision, CancellationToken ct)
    {
        using var response = await fixture.Stub.PostAsJsonAsync("/__e2e/gitops-revision", new { revision }, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task PushAsync(string commit, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            @ref = "refs/heads/main",
            after = commit,
            installation = new { id = 42 },
            repository = new { id = 9002 }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integrations/github/webhook")
        { Content = new ByteArrayContent(bytes) };
        request.Headers.Add("X-GitHub-Event", "push");
        request.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());
        request.Headers.Add("X-Hub-Signature-256",
            "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(fixture.GitHubWebhookSecret),
                bytes)));
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private Task<JsonElement> AppliedAsync(string source, string commit, CancellationToken ct) =>
        WaitAsync(source,
            item => item.GetProperty("synchronization").GetProperty("fullyAppliedCommit").GetString()
                == commit && item.GetProperty("status").GetString() == "ConfigurationApplied",
            ct);

    private async Task<JsonElement> WaitAsync(string source, Func<JsonElement, bool> ready, CancellationToken ct)
    {
        while (true)
        {
            var current = await JsonAsync(HttpMethod.Get, $"{Root}/{source}", null, ct);
            if (ready(current))
                return current;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task<JsonElement> JsonAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(method,
            path,
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await fixture.Api.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
}
