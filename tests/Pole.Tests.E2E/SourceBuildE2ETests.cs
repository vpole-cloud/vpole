using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
[Trait("Layer", "E2E")]
[Trait("Concern", "SourceBuild")]
[Trait("Feature", "SourceBuilds")]
[Trait("Scope", "FullE2E")]
public sealed class SourceBuildE2ETests(PoleE2EFixture fixture)
{
    private const string RevisionOneCommitSha = "1111111111111111111111111111111111111111";
    private const string RevisionTwoCommitSha = "2222222222222222222222222222222222222222";

    [SourceBuildFact]
    public async Task Source_build_uses_the_exact_repository_commit_pushes_an_artifact_and_records_release_provenance()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var cancellationToken = cancellation.Token;
        await fixture.EnsureSourceBuildInfrastructureReadyAsync(cancellationToken);

        string? resourceGroupId = null;
        CreatedWebService? webService = null;
        string? sourceRepositoryId = null;
        string? sourceBuildConfigurationId = null;
        try
        {
            resourceGroupId = await CreateResourceGroupAsync(cancellationToken);
            webService = await CreateWebServiceAsync(resourceGroupId, cancellationToken);
            await fixture.WaitForNamespaceCreationAsync(resourceGroupId, cancellationToken);
            await WaitForReleaseOutcomeAsync(webService.Id, "Succeeded", cancellationToken);

            var connectionId = await EnsureGitHubConnectionAsync(cancellationToken);
            sourceRepositoryId = await LinkSourceRepositoryAsync(connectionId, cancellationToken);
            sourceBuildConfigurationId = await CreateSourceBuildConfigurationAsync(sourceRepositoryId, webService.Id, cancellationToken);

            var v1Build = await StartBuildAsync(sourceBuildConfigurationId, cancellationToken);
            var v1CompletedBuild = await WaitForBuildAsync(v1Build, "succeeded", cancellationToken);
            var v1State = v1CompletedBuild.GetProperty("state");
            var v1CommitSha = RequiredString(v1CompletedBuild, "commitSha");
            var v1ImageDigest = RequiredString(v1State, "imageDigest");
            var v1ImageReference = RequiredString(v1State, "imageReference");
            var v1ReleaseId = RequiredString(v1State, "releaseId");
            Assert.Equal(RevisionOneCommitSha, v1CommitSha);

            var v1ArtifactDigest = await fixture.GetHarborArtifactDigestAsync(
                fixture.Alpha.DefaultTenantId,
                webService.Id,
                v1Build,
                cancellationToken);
            Assert.Equal(v1ImageDigest, v1ArtifactDigest);
            await WaitForReleaseOutcomeAsync(webService.Id, "Succeeded", cancellationToken, v1ReleaseId);
            var v1Releases = await GetReleasesAsync(webService.Id, cancellationToken);
            var v1Release = FindRelease(v1Releases, v1ReleaseId);
            AssertSourceBuildRelease(v1Release, v1ReleaseId, v1CommitSha, v1ImageDigest, v1ImageReference);
            await AssertSourceBuildDeploymentAsync(
                resourceGroupId,
                webService,
                v1ImageReference,
                v1ReleaseId,
                cancellationToken);

            await SetStubRevisionAsync("v2", cancellationToken);
            await ReanalyzeSourceRepositoryAsync(sourceRepositoryId, RevisionTwoCommitSha, cancellationToken);

            var v2Build = await StartBuildAsync(sourceBuildConfigurationId, cancellationToken);
            var v2CompletedBuild = await WaitForBuildAsync(v2Build, "succeeded", cancellationToken);
            var v2State = v2CompletedBuild.GetProperty("state");
            var v2CommitSha = RequiredString(v2CompletedBuild, "commitSha");
            var v2ImageDigest = RequiredString(v2State, "imageDigest");
            var v2ImageReference = RequiredString(v2State, "imageReference");
            var v2ReleaseId = RequiredString(v2State, "releaseId");
            Assert.Equal(RevisionTwoCommitSha, v2CommitSha);
            Assert.NotEqual(v1ImageDigest, v2ImageDigest);

            var v2ArtifactDigest = await fixture.GetHarborArtifactDigestAsync(
                fixture.Alpha.DefaultTenantId,
                webService.Id,
                v2Build,
                cancellationToken);
            Assert.Equal(v2ImageDigest, v2ArtifactDigest);
            await WaitForReleaseOutcomeAsync(webService.Id, "Succeeded", cancellationToken, v2ReleaseId);
            var releases = await GetReleasesAsync(webService.Id, cancellationToken);
            AssertSourceBuildRelease(FindRelease(releases, v1ReleaseId), v1ReleaseId, v1CommitSha, v1ImageDigest, v1ImageReference);
            AssertSourceBuildRelease(FindRelease(releases, v2ReleaseId), v2ReleaseId, v2CommitSha, v2ImageDigest, v2ImageReference);
            await AssertSourceBuildDeploymentAsync(
                resourceGroupId,
                webService,
                v2ImageReference,
                v2ReleaseId,
                cancellationToken);

            await fixture.StopBuildKitAccessAsync();
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
            if (webService is not null)
                await TryCleanupAsync(() => DeleteAsync($"/api/v1/web-services/{webService.Id}", cleanupToken), cleanupFailures);
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

    private async Task<CreatedWebService> CreateWebServiceAsync(string resourceGroupId, CancellationToken cancellationToken)
    {
        var name = $"sb-{Guid.NewGuid():N}"[..20];
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Post, "/api/v1/web-services", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        request.Content = JsonContent.Create(new
        {
            name,
            resourceGroupId,
            tags = new[] { "e2e", "source-build" },
            spec = new
            {
                image = new { server = "docker.io", name = "library/nginx", tag = "1.27" },
                config = new
                {
                    environmentVariables = new Dictionary<string, string>(),
                    portMappings = new[] { new { servicePort = 80, targetPort = "80", protocol = "TCP", name = "http" } }
                },
                autoscaling = (object?)null,
                probes = new
                {
                    startup = Probe(),
                    liveness = Probe(),
                    readiness = Probe()
                }
            },
            placement = new
            {
                isolationScopeId = (string?)null,
                targets = new[]
                {
                    new
                    {
                        deploymentTargetId = (string?)null,
                        fixedReplicas = 1,
                        computeProfileId = (string?)null
                    }
                }
            }
        });
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var id = document.RootElement.GetProperty("webServiceId").GetString()
                 ?? throw new InvalidOperationException("Pole did not return a web service id.");
        return new CreatedWebService(id, name);
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
        if (!analysisResponse.IsSuccessStatusCode)
        {
            var diagnostics = await fixture.GetServerErrorDiagnosticsAsync(cancellationToken);
            var stubRequests = await fixture.GetGitHubStubRequestDiagnosticsAsync(cancellationToken);
            var responseDetails = await ReadSafeErrorDetailsAsync(analysisResponse, cancellationToken);
            throw new HttpRequestException(
                $"Pole could not analyze the source repository: {(int)analysisResponse.StatusCode}." +
                (string.IsNullOrWhiteSpace(responseDetails)
                    ? string.Empty
                    : $"{Environment.NewLine}API detail: {responseDetails}") +
                (string.IsNullOrWhiteSpace(diagnostics)
                    ? string.Empty
                    : $"{Environment.NewLine}Server diagnostic: {diagnostics}") +
                (string.IsNullOrWhiteSpace(stubRequests)
                    ? string.Empty
                    : $"{Environment.NewLine}GitHub stub requests:{Environment.NewLine}{stubRequests}"),
                null,
                analysisResponse.StatusCode);
        }
        analysisResponse.EnsureSuccessStatusCode();
        using var analysis = JsonDocument.Parse(await analysisResponse.Content.ReadAsStreamAsync(cancellationToken));
        var candidate = analysis.RootElement.GetProperty("candidates")[0];
        Assert.Contains("FROM scratch", RequiredString(candidate, "dockerfilePreview"), StringComparison.Ordinal);
        var candidateKey = RequiredString(candidate, "candidateKey");

        using var link = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/v1/git-provider-connections/{connectionId}/repositories/{repositoryId}/links",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        link.Content = JsonContent.Create(new { branch = "main", candidateKey });
        using var linkResponse = await fixture.Api.SendAsync(link, cancellationToken);
        if (!linkResponse.IsSuccessStatusCode)
        {
            var diagnostics = await fixture.GetServerErrorDiagnosticsAsync(cancellationToken);
            var stubRequests = await fixture.GetGitHubStubRequestDiagnosticsAsync(cancellationToken);
            var responseDetails = await ReadSafeErrorDetailsAsync(linkResponse, cancellationToken);
            throw new HttpRequestException(
                $"Pole could not link the analyzed source repository: {(int)linkResponse.StatusCode}." +
                (string.IsNullOrWhiteSpace(responseDetails)
                    ? string.Empty
                    : $"{Environment.NewLine}API detail: {responseDetails}") +
                (string.IsNullOrWhiteSpace(diagnostics)
                    ? string.Empty
                    : $"{Environment.NewLine}Server diagnostic: {diagnostics}") +
                (string.IsNullOrWhiteSpace(stubRequests)
                    ? string.Empty
                    : $"{Environment.NewLine}GitHub stub requests:{Environment.NewLine}{stubRequests}"),
                null,
                linkResponse.StatusCode);
        }
        linkResponse.EnsureSuccessStatusCode();
        using var source = JsonDocument.Parse(await linkResponse.Content.ReadAsStreamAsync(cancellationToken));
        return source.RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("Pole did not return a source repository id.");
    }

    private async Task SetStubRevisionAsync(string revision, CancellationToken cancellationToken)
    {
        using var response = await fixture.Stub.PostAsJsonAsync(
            "/__e2e/revision",
            new { revision },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task ReanalyzeSourceRepositoryAsync(
        string sourceRepositoryId,
        string expectedCommitSha,
        CancellationToken cancellationToken)
    {
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/v1/source-repositories/{sourceRepositoryId}/reanalyze",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var sourceRepository = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        Assert.Equal(expectedCommitSha, RequiredString(sourceRepository.RootElement, "commitSha"));
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
            {
                var failedState = job.GetProperty("state");
                var failureCode = failedState.GetProperty("failureCode").GetString();
                var failureMessage = failedState.GetProperty("failureMessage").GetString();
                var diagnostics = await fixture.GetServerErrorDiagnosticsAsync(cancellationToken);
                throw new Xunit.Sdk.XunitException(
                    $"Expected build '{buildJobId}' to succeed, but it failed. " +
                    $"Code: {failureCode ?? "<none>"}. " +
                    $"Message: {SanitizeDiagnostic(failureMessage)}" +
                    (string.IsNullOrWhiteSpace(diagnostics)
                        ? string.Empty
                        : $"{Environment.NewLine}Server diagnostic: {diagnostics}"));
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task WaitForReleaseOutcomeAsync(
        string webServiceId,
        string expectedOutcome,
        CancellationToken cancellationToken,
        string? releaseId = null)
    {
        while (true)
        {
            var releases = await GetReleasesAsync(webServiceId, cancellationToken);
            foreach (var release in releases.EnumerateArray())
            {
                if (releaseId is not null &&
                    !string.Equals(release.GetProperty("id").GetString(), releaseId, StringComparison.Ordinal))
                {
                    continue;
                }

                var outcome = release.GetProperty("outcome").GetString();
                if (outcome == expectedOutcome)
                    return;
                if (outcome == "Failed")
                {
                    var failureMessage = release.GetProperty("failureMessage").GetString();
                    var diagnostics = await fixture.GetWebServiceDeploymentDiagnosticsAsync(cancellationToken);
                    throw new Xunit.Sdk.XunitException(
                        $"Web service release failed before it reached {expectedOutcome}: {SanitizeDiagnostic(failureMessage)}" +
                        (string.IsNullOrWhiteSpace(diagnostics)
                            ? string.Empty
                            : $"{Environment.NewLine}Server diagnostic: {diagnostics}"));
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task<JsonElement> GetReleasesAsync(string webServiceId, CancellationToken cancellationToken)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/web-services/{webServiceId}/releases", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.GetProperty("releases").GetProperty("items").Clone();
    }

    private async Task AssertSourceBuildDeploymentAsync(
        string resourceGroupId,
        CreatedWebService webService,
        string expectedImageReference,
        string expectedReleaseId,
        CancellationToken cancellationToken)
    {
        var deployment = await fixture.GetWebServiceDeploymentAsync(resourceGroupId, webService.Name, cancellationToken);
        var podTemplate = deployment.GetProperty("spec").GetProperty("template");
        var podSpec = podTemplate.GetProperty("spec");
        Assert.Equal(
            expectedImageReference,
            RequiredString(podSpec.GetProperty("containers")[0], "image"));
        Assert.Equal(
            expectedReleaseId,
            RequiredString(podTemplate.GetProperty("metadata").GetProperty("annotations"), "pole.server/releaseId"));

        var pullSecrets = podSpec.GetProperty("imagePullSecrets");
        Assert.True(pullSecrets.GetArrayLength() > 0, "The source-built Deployment should reference its Harbor pull secret.");
        var pullSecretName = RequiredString(pullSecrets[0], "name");
        Assert.True(
            await fixture.KubernetesSecretExistsAsync(resourceGroupId, pullSecretName, cancellationToken),
            "The source-built Deployment should have its Harbor pull secret in the resource-group namespace.");
    }

    private static JsonElement FindRelease(JsonElement releases, string releaseId)
    {
        var release = releases.EnumerateArray().SingleOrDefault(item =>
            string.Equals(item.GetProperty("id").GetString(), releaseId, StringComparison.Ordinal));
        Assert.NotEqual(JsonValueKind.Undefined, release.ValueKind);
        return release;
    }

    private static void AssertSourceBuildRelease(
        JsonElement release,
        string expectedReleaseId,
        string expectedCommitSha,
        string expectedImageDigest,
        string expectedImageReference)
    {
        Assert.Equal(expectedReleaseId, RequiredString(release, "id"));
        Assert.Equal("SourceBuild", RequiredString(release, "action"));
        Assert.Equal("Succeeded", RequiredString(release, "outcome"));
        var provenance = release.GetProperty("provenance");
        Assert.Equal("PoleSourceBuild", RequiredString(provenance, "source"));
        Assert.Equal(expectedCommitSha, RequiredString(provenance, "commitSha"));
        Assert.Equal(expectedImageDigest, RequiredString(provenance, "imageDigest"));
        Assert.Equal(expectedImageReference, RequiredString(provenance, "newImageTag"));
    }

    private async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return;
            if (response.StatusCode != System.Net.HttpStatusCode.Conflict)
                response.EnsureSuccessStatusCode();

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
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

    private static string SanitizeDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<none>";

        var redacted = Regex.Replace(
            Regex.Replace(
                Regex.Replace(value, "(?i)(authorization)\\s*[:=]\\s*(?:Basic|Bearer)\\s+\\S+", "$1=[redacted]"),
                "(?i)Bearer\\s+\\S+",
                "Bearer [redacted]"),
            "(?i)(password|token|secret)(?:[\\\"']\\s*)?[:=]\\s*(?:[\\\"'])?[^\\s,}\\]\\\"']+",
            "$1=[redacted]");
        return redacted.Length <= 1_000 ? redacted : $"{redacted[..1_000]}…";
    }

    private static async Task<string> ReadSafeErrorDetailsAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType is not "application/json")
            return string.Empty;

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var properties = new[] { "message", "serviceName", "details" }
            .Where(property => document.RootElement.TryGetProperty(property, out var value) &&
                               value.ValueKind == JsonValueKind.String)
            .Select(property => $"{property}={SanitizeDiagnostic(document.RootElement.GetProperty(property).GetString())}");
        return string.Join(", ", properties);
    }

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

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString()
        ?? throw new InvalidOperationException($"Pole did not return '{propertyName}'.");

    private sealed record CreatedWebService(string Id, string Name);
}
