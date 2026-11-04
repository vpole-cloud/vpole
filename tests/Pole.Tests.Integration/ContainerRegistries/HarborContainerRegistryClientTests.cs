using System.Net;
using System.Text;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Harbor;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.ContainerRegistries;

public sealed class HarborContainerRegistryClientTests
{
    private const string AttemptId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Provisioning_CreatesPrivateProjectAndLeastPrivilegeRobots()
    {
        var projectLookups = 0;
        var robotBodies = new List<string>();
        var handler = new DelegateHandler(async request =>
        {
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v2.0/projects")
            {
                projectLookups++;
                return Json(HttpStatusCode.OK, projectLookups == 1
                    ? "[]"
                    : """[{ "project_id": 17, "name": "pole-test" }]""");
            }

            if (request.Method == HttpMethod.Post && path == "/api/v2.0/projects")
            {
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains("\"project_name\":\"pole-test\"", body, StringComparison.Ordinal);
                Assert.Contains("\"public\":false", body, StringComparison.Ordinal);
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v2.0/robots", path);
            robotBodies.Add(await request.Content!.ReadAsStringAsync());
            var id = robotBodies.Count == 1 ? 21 : 22;
            var name = robotBodies.Count == 1 ? "robot$build" : "robot$runtime";
            var secret = robotBodies.Count == 1 ? "build-secret" : "pull-secret";
            return Json(HttpStatusCode.Created, $$"""
                { "id": {{id}}, "name": "{{name}}", "secret": "{{secret}}", "expires_at": -1 }
                """);
        });
        var client = CreateClient(handler);

        var result = await client.ProvisionProjectAsync(
            new RegistryProjectProvisioningRequest("tenant", "pole-test", AttemptId));

        var provisioned = Assert.IsType<ProvisionedRegistryProject>(result.Value);
        Assert.Equal("17", provisioned.ExternalProjectId);
        Assert.Equal("build-secret", provisioned.BuildCredential.Secret);
        Assert.Equal("pull-secret", provisioned.PullCredential.Secret);
        Assert.Equal(2, robotBodies.Count);
        Assert.Contains($"\"name\":\"pole-build-{AttemptId}\"", robotBodies[0], StringComparison.Ordinal);
        Assert.Contains($"\"name\":\"pole-runtime-{AttemptId}\"", robotBodies[1], StringComparison.Ordinal);
        Assert.Contains("\"namespace\":\"pole-test\"", robotBodies[0], StringComparison.Ordinal);
        Assert.Contains("\"action\":\"push\"", robotBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\"action\":\"push\"", robotBodies[1], StringComparison.Ordinal);
        Assert.Contains("\"action\":\"pull\"", robotBodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialRobotFailure_ReturnsFailureForDurableCallerOwnedCleanup()
    {
        var robotRequests = 0;
        var handler = new DelegateHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, """[{ "project_id": 17, "name": "pole-test" }]""");
            robotRequests++;
            _ = await request.Content!.ReadAsStringAsync();
            return robotRequests == 1
                ? Json(HttpStatusCode.Created, """
                    { "id": 21, "name": "robot$build", "secret": "build-secret", "expires_at": -1 }
                    """)
                : Json(HttpStatusCode.InternalServerError, """{ "message": "build-secret leaked upstream" }""");
        });
        var client = CreateClient(handler);

        var result = await client.ProvisionProjectAsync(
            new RegistryProjectProvisioningRequest("tenant", "pole-test", AttemptId));

        var failed = Assert.IsType<ContainerRegistryProviderError>(result.Value);
        Assert.Equal(2, robotRequests);
        Assert.IsType<ContainerRegistryProviderUnavailable>(failed.Value);
        Assert.DoesNotContain("build-secret", failed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullRobotTimeout_ReturnsFailureForDurableCallerOwnedCleanup()
    {
        var robotRequests = 0;
        var handler = new DelegateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Json(
                    HttpStatusCode.OK,
                    """[{ "project_id": 17, "name": "pole-test" }]"""));
            }

            robotRequests++;
            return robotRequests == 1
                ? Task.FromResult(Json(HttpStatusCode.Created, """
                    { "id": 21, "name": "robot$build", "secret": "build-secret", "expires_at": -1 }
                    """))
                : Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated timeout"));
        });
        var client = CreateClient(handler);

        var result = await client.ProvisionProjectAsync(
            new RegistryProjectProvisioningRequest("tenant", "pole-test", AttemptId));

        var failed = Assert.IsType<ContainerRegistryProviderError>(result.Value);
        Assert.Equal(2, robotRequests);
        Assert.IsType<ContainerRegistryProviderUnavailable>(failed.Value);
    }

    [Fact]
    public async Task ArtifactLookup_MapsDigestAndDoubleEscapesNestedRepositoryName()
    {
        var handler = new DelegateHandler(request =>
        {
            Assert.Equal(
                "/api/v2.0/projects/pole-test/repositories/service%252Fapi/artifacts/build-42",
                request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(
                HttpStatusCode.OK,
                """{ "digest": "sha256:abc", "size": 1234 }"""));
        });
        var client = CreateClient(handler);

        var result = await client.GetArtifactAsync(
            new GetRegistryArtifactRequest(
                ProjectName: "pole-test",
                RepositoryName: "service/api",
                Reference: "build-42"));

        var artifact = Assert.IsType<RegistryArtifact>(result.Value);
        Assert.Equal("sha256:abc", artifact.Digest);
        Assert.Equal(1234, artifact.SizeBytes);
    }

    [Fact]
    public async Task RobotCleanupAttemptsEveryRobotAndReturnsFailureWithoutDeletingProjectResources()
    {
        var calls = new List<string>();
        var handler = new DelegateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add($"{request.Method} {path}");
            return Task.FromResult((request.Method, path) switch
            {
                ({ } method, "/api/v2.0/robots/21") when method == HttpMethod.Delete =>
                    new HttpResponseMessage(HttpStatusCode.InternalServerError),
                ({ } method, "/api/v2.0/robots/22") when method == HttpMethod.Delete =>
                    new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected Harbor request: {request.Method} {path}")
            });
        });
        var client = CreateClient(handler);

        var result = await client.DeleteRobotCredentialsAsync(
        [
            new RegistryRobotCleanupTarget("pole-test", $"pole-build-{AttemptId}", "21"),
            new RegistryRobotCleanupTarget("pole-test", $"pole-runtime-{AttemptId}", "22")
        ]);

        var failed = Assert.IsType<ContainerRegistryProviderError>(result.Value);
        Assert.IsType<ContainerRegistryProviderUnavailable>(failed.Value);
        Assert.Equal(
            [
                "DELETE /api/v2.0/robots/21",
                "DELETE /api/v2.0/robots/22"
            ],
            calls);
        Assert.DoesNotContain(calls, call => call.Contains("/projects/", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, call => call.Contains("/repositories/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RobotCleanup_DiscoversRobotByDeterministicNameWhenCreateResponseWasLost()
    {
        var robotName = $"pole-build-{AttemptId}";
        var calls = new List<string>();
        var handler = new DelegateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add($"{request.Method} {path}{request.RequestUri.Query}");
            return Task.FromResult((request.Method, path) switch
            {
                ({ } method, "/api/v2.0/robots") when method == HttpMethod.Get =>
                    Json(HttpStatusCode.OK, $$"""
                        [
                          {
                            "id": 21,
                            "name": "custom-prefix-project+{{robotName}}",
                            "permissions": [{ "namespace": "project" }]
                          },
                          {
                            "id": 22,
                            "name": "custom-prefix-other-project+{{robotName}}",
                            "permissions": [{ "namespace": "other-project" }]
                          }
                        ]
                        """),
                ({ } method, "/api/v2.0/robots/21") when method == HttpMethod.Delete =>
                    new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected Harbor request: {request.Method} {path}")
            });
        });
        var client = CreateClient(handler);

        var result = await client.DeleteRobotCredentialsAsync(
            [new RegistryRobotCleanupTarget("project", robotName, null)]);

        var cleanup = Assert.IsType<RegistryRobotCleanupResult>(result.Value);
        Assert.Equal(1, cleanup.MatchedByNameCount);
        Assert.Equal(
            [
                $"GET /api/v2.0/robots?q=name%3D{robotName}&page=1&page_size=100",
                "DELETE /api/v2.0/robots/21"
            ],
            calls);
    }

    [Fact]
    public async Task DeletingProject_DeletesRepositoriesBeforeDeletingProject()
    {
        var calls = new List<string>();
        var handler = new DelegateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add($"{request.Method} {path}{request.RequestUri.Query}");
            return (request.Method, path) switch
            {
                ({ } method, "/api/v2.0/projects/17") when method == HttpMethod.Get =>
                    Task.FromResult(Json(HttpStatusCode.OK, """{ "project_id": 17, "name": "pole-test" }""")),
                ({ } method, "/api/v2.0/projects/pole-test/repositories") when method == HttpMethod.Get =>
                    Task.FromResult(Json(HttpStatusCode.OK, """
                        [{ "name": "pole-test/web-services/service-1" }]
                        """)),
                ({ } method, "/api/v2.0/projects/pole-test/repositories/web-services%252Fservice-1") when method == HttpMethod.Delete =>
                    Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
                ({ } method, "/api/v2.0/projects/17") when method == HttpMethod.Delete =>
                    Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected Harbor request: {request.Method} {path}")
            };
        });
        var client = CreateClient(handler);

        var result = await client.DeleteProjectAsync("17");

        Assert.IsType<RegistryProjectDeleted>(result.Value);
        Assert.Equal(
            [
                "GET /api/v2.0/projects/17",
                "GET /api/v2.0/projects/pole-test/repositories?page=1&page_size=100",
                "DELETE /api/v2.0/projects/pole-test/repositories/web-services%252Fservice-1",
                "DELETE /api/v2.0/projects/17"
            ],
            calls);
    }

    [Fact]
    public async Task NetworkFailure_DoesNotExposeTransportMessage()
    {
        var client = CreateClient(new ThrowingHandler());

        var result = await client.GetArtifactAsync(
            new GetRegistryArtifactRequest(
                ProjectName: "pole-test",
                RepositoryName: "service",
                Reference: "tag"));

        var failed = Assert.IsType<ContainerRegistryArtifactLookupError>(result.Value);
        var providerError = Assert.IsType<ContainerRegistryProviderError>(failed.Value);
        Assert.IsType<ContainerRegistryProviderUnavailable>(providerError.Value);
        Assert.DoesNotContain("admin-secret", failed.ToString(), StringComparison.Ordinal);
    }

    private static HarborContainerRegistryClient CreateClient(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new HarborOptions
            {
                Enabled = true,
                BaseUrl = "https://harbor.test",
                RegistryHost = "harbor.test",
                Username = "admin",
                Password = "admin-secret"
            });

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responseFactory(request);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("transport details containing admin-secret");
    }
}
