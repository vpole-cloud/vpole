using System.Net.Http.Json;
using System.Text.Json;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class SourceBuildE2ETests(PoleE2EFixture fixture)
{
    [SourceBuildFact]
    public async Task Source_build_uses_the_exact_repository_commit_pushes_an_artifact_and_records_release_provenance()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var cancellationToken = cancellation.Token;
        await fixture.StartSourceBuildInfrastructureAsync(cancellationToken);

        string? resourceGroupId = null;
        string? webServiceId = null;
        string? sourceRepositoryId = null;
        string? sourceBuildConfigurationId = null;
        try
        {
            resourceGroupId = await CreateResourceGroupAsync(cancellationToken);
            webServiceId = await CreateWebServiceAsync(resourceGroupId, cancellationToken);
            await WaitForReleaseOutcomeAsync(webServiceId, "Succeeded", cancellationToken);

            var connectionId = await EnsureGitHubConnectionAsync(cancellationToken);
            sourceRepositoryId = await LinkSourceRepositoryAsync(connectionId, cancellationToken);
            sourceBuildConfigurationId = await CreateSourceBuildConfigurationAsync(sourceRepositoryId, webServiceId, cancellationToken);

            var succeededBuild = await StartBuildAsync(sourceBuildConfigurationId, cancellationToken);
            var completedBuild = await WaitForBuildAsync(succeededBuild, "succeeded", cancellationToken);
            var commitSha = completedBuild.GetProperty("commitSha").GetString();
            var imageDigest = completedBuild.GetProperty("state").GetProperty("imageDigest").GetString();
            Assert.False(string.IsNullOrWhiteSpace(commitSha));
            Assert.False(string.IsNullOrWhiteSpace(imageDigest));

            var artifactDigest = await fixture.GetHarborArtifactDigestAsync(
                fixture.Alpha.DefaultTenantId,
                webServiceId,
                succeededBuild,
                cancellationToken);
            Assert.Equal(imageDigest, artifactDigest);

            var releases = await GetReleasesAsync(webServiceId, cancellationToken);
            var sourceBuildRelease = releases.EnumerateArray().FirstOrDefault(release =>
                release.GetProperty("action").GetString() == "SourceBuild" &&
                release.GetProperty("provenance").GetProperty("source").GetString() == "PoleSourceBuild");
            Assert.NotEqual(JsonValueKind.Undefined, sourceBuildRelease.ValueKind);
            Assert.Equal(commitSha, sourceBuildRelease.GetProperty("provenance").GetProperty("commitSha").GetString());
            Assert.Equal(imageDigest, sourceBuildRelease.GetProperty("provenance").GetProperty("imageDigest").GetString());

            await fixture.StopBuildKitAsync(cancellationToken);
            var failedBuild = await StartBuildAsync(sourceBuildConfigurationId, cancellationToken);
            var failed = await WaitForBuildAsync(failedBuild, "failed", cancellationToken);
            var failedState = failed.GetProperty("state");
            Assert.False(string.IsNullOrWhiteSpace(failedState.GetProperty("failureCode").GetString()));
            Assert.DoesNotContain("password", failedState.GetProperty("failureMessage").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var cleanupToken = cleanup.Token;
            var cleanupFailures = new List<Exception>();
            if (!string.IsNullOrWhiteSpace(sourceBuildConfigurationId))
                await TryCleanupAsync(() => DeleteAsync($"/api/v1/source-build-configurations/{sourceBuildConfigurationId}", cleanupToken), cleanupFailures);
            if (!string.IsNullOrWhiteSpace(sourceRepositoryId))
                await TryCleanupAsync(() => DeleteAsync($"/api/v1/source-repositories/{sourceRepositoryId}", cleanupToken), cleanupFailures);
            if (!string.IsNullOrWhiteSpace(webServiceId))
                await TryCleanupAsync(() => DeleteAsync($"/api/v1/web-services/{webServiceId}", cleanupToken), cleanupFailures);
            if (!string.IsNullOrWhiteSpace(resourceGroupId))
            {
                await TryCleanupAsync(() => DeleteAsync($"/api/v1/resource-groups/{resourceGroupId}", cleanupToken), cleanupFailures);
                await TryCleanupAsync(() => fixture.WaitForNamespaceDeletionAsync(resourceGroupId, cleanupToken), cleanupFailures);
            }

            await TryCleanupAsync(() => fixture.DeleteHarborProjectAsync(fixture.Alpha.DefaultTenantId, cleanupToken), cleanupFailures);
            if (cleanupFailures.Count > 0)
                throw new AggregateException("The source-build E2E cleanup did not complete.", cleanupFailures);
        }
    }

    private async Task<string> CreateResourceGroupAsync(CancellationToken cancellationToken)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Post, "/api/v1/resource-groups", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        request.Content = JsonContent.Create(new
        {
            name = $"sb-{Guid.NewGuid():N}"[..20],
            description = "Source-build E2E resource group",
            tags = new[] { "e2e", "source-build" }
        });
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("Pole did not return a resource group id.");
    }

    private async Task<string> CreateWebServiceAsync(string resourceGroupId, CancellationToken cancellationToken)
    {
        var name = $"sb-{Guid.NewGuid():N}"[..20];
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Post, "/api/v1/web-services", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        request.Content = JsonContent.Create(new
        {
            name,
            resourceGroupId,
            domainName = $"{name}.e2e.pole.test",
            tags = new[] { "e2e", "source-build" },
            spec = new
            {
                image = new { server = "docker.io", name = "library/nginx", tag = "1.27" },
                replicas = 1,
                resources = new
                {
                    requests = new { cpuCores = 0.1, memoryMi = 64 },
                    limits = new { cpuCores = 0.25, memoryMi = 128 }
                },
                config = new
                {
                    environmentVariables = new Dictionary<string, string>(),
                    portMappings = new[] { new { servicePort = 80, targetPort = "8080", protocol = "TCP", name = "http" } }
                },
                autoscaling = (object?)null,
                probes = new
                {
                    startup = Probe(),
                    liveness = Probe(),
                    readiness = Probe()
                }
            }
        });
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.GetProperty("webServiceId").GetString()
               ?? throw new InvalidOperationException("Pole did not return a web service id.");
    }

    private async Task<string> EnsureGitHubConnectionAsync(CancellationToken cancellationToken)
    {
        using var list = fixture.CreateAuthenticatedRequest(HttpMethod.Get, "/api/v1/git-provider-connections", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var listResponse = await fixture.Api.SendAsync(list, cancellationToken);
        listResponse.EnsureSuccessStatusCode();
        using var connections = JsonDocument.Parse(await listResponse.Content.ReadAsStreamAsync(cancellationToken));
        var existing = connections.RootElement.EnumerateArray().FirstOrDefault();
        if (existing.ValueKind != JsonValueKind.Undefined)
            return existing.GetProperty("id").GetString()!;

        using var begin = fixture.CreateAuthenticatedRequest(HttpMethod.Post, "/api/v1/git-provider-connections/github/install", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var beginResponse = await fixture.Api.SendAsync(begin, cancellationToken);
        beginResponse.EnsureSuccessStatusCode();
        using var beginJson = JsonDocument.Parse(await beginResponse.Content.ReadAsStreamAsync(cancellationToken));
        using var callback = await fixture.Stub.GetAsync(beginJson.RootElement.GetProperty("installUrl").GetString(), cancellationToken);
        callback.EnsureSuccessStatusCode();

        using var verifiedList = fixture.CreateAuthenticatedRequest(HttpMethod.Get, "/api/v1/git-provider-connections", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var verifiedResponse = await fixture.Api.SendAsync(verifiedList, cancellationToken);
        verifiedResponse.EnsureSuccessStatusCode();
        using var verified = JsonDocument.Parse(await verifiedResponse.Content.ReadAsStreamAsync(cancellationToken));
        return verified.RootElement.EnumerateArray().Single().GetProperty("id").GetString()!;
    }

    private async Task<string> LinkSourceRepositoryAsync(string connectionId, CancellationToken cancellationToken)
    {
        const string repositoryId = "9001";
        using var analyze = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/v1/git-provider-connections/{connectionId}/repositories/{repositoryId}/analysis",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        analyze.Content = JsonContent.Create(new { branch = "main" });
        using var analysisResponse = await fixture.Api.SendAsync(analyze, cancellationToken);
        analysisResponse.EnsureSuccessStatusCode();
        using var analysis = JsonDocument.Parse(await analysisResponse.Content.ReadAsStreamAsync(cancellationToken));
        var candidateKey = analysis.RootElement.GetProperty("candidates")[0].GetProperty("candidateKey").GetString();

        using var link = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/v1/git-provider-connections/{connectionId}/repositories/{repositoryId}/links",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        link.Content = JsonContent.Create(new { branch = "main", candidateKey });
        using var linkResponse = await fixture.Api.SendAsync(link, cancellationToken);
        linkResponse.EnsureSuccessStatusCode();
        using var source = JsonDocument.Parse(await linkResponse.Content.ReadAsStreamAsync(cancellationToken));
        return source.RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("Pole did not return a source repository id.");
    }

    private async Task<string> CreateSourceBuildConfigurationAsync(string sourceRepositoryId, string webServiceId, CancellationToken cancellationToken)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Post, "/api/v1/source-build-configurations", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        request.Content = JsonContent.Create(new { sourceRepositoryId, webServiceId });
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var configuration = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return configuration.RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("Pole did not return a source build configuration id.");
    }

    private async Task<string> StartBuildAsync(string configurationId, CancellationToken cancellationToken)
    {
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/v1/source-build-configurations/{configurationId}/builds",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var job = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return job.RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("Pole did not return a source build id.");
    }

    private async Task<JsonElement> WaitForBuildAsync(string buildJobId, string status, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/source-build-jobs/{buildJobId}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var job = document.RootElement.Clone();
            var currentStatus = job.GetProperty("state").GetProperty("status").GetString();
            if (string.Equals(currentStatus, status, StringComparison.OrdinalIgnoreCase))
                return job;
            if (string.Equals(currentStatus, "failed", StringComparison.OrdinalIgnoreCase))
                throw new Xunit.Sdk.XunitException($"Expected build '{buildJobId}' to succeed, but it failed.");

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task WaitForReleaseOutcomeAsync(string webServiceId, string expectedOutcome, CancellationToken cancellationToken)
    {
        while (true)
        {
            var releases = await GetReleasesAsync(webServiceId, cancellationToken);
            if (releases.EnumerateArray().Any(release => release.GetProperty("outcome").GetString() == expectedOutcome))
                return;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task<JsonElement> GetReleasesAsync(string webServiceId, CancellationToken cancellationToken)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/web-services/{webServiceId}/deployments", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.GetProperty("releases").GetProperty("items").Clone();
    }

    private async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    private static object Probe() => new
    {
        path = "/",
        port = "8080",
        initialDelaySeconds = 0,
        periodSeconds = 5,
        timeoutSeconds = 3,
        failureThreshold = 3,
        successThreshold = 1
    };

    private static async Task TryCleanupAsync(Func<Task> action, ICollection<Exception> failures)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }
}
