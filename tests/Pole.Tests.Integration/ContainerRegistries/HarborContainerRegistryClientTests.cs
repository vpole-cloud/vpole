using System.Net;
using System.Text;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Harbor;
using Pole.Features.ContainerRegistries.Contracts;
using Xunit;

namespace Pole.Tests.Integration.ContainerRegistries;

public sealed class HarborContainerRegistryClientTests
{
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

        var result = await client.ProvisionProjectAsync(new RegistryProjectProvisioningRequest("tenant", "pole-test"));

        Assert.True(result.TryGetValue(out var provisioned, out _));
        Assert.Equal("17", provisioned.ExternalProjectId);
        Assert.Equal("build-secret", provisioned.BuildCredential.Secret);
        Assert.Equal("pull-secret", provisioned.PullCredential.Secret);
        Assert.Equal(2, robotBodies.Count);
        Assert.Contains("\"namespace\":\"pole-test\"", robotBodies[0], StringComparison.Ordinal);
        Assert.Contains("\"action\":\"push\"", robotBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\"action\":\"push\"", robotBodies[1], StringComparison.Ordinal);
        Assert.Contains("\"action\":\"pull\"", robotBodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialRobotFailure_DeletesCreatedRobotAndRedactsResponseBody()
    {
        var robotRequests = 0;
        var deletedRobot = false;
        var handler = new DelegateHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, """[{ "project_id": 17, "name": "pole-test" }]""");
            if (request.Method == HttpMethod.Delete)
            {
                Assert.Equal("/api/v2.0/robots/21", path);
                deletedRobot = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            robotRequests++;
            _ = await request.Content!.ReadAsStringAsync();
            return robotRequests == 1
                ? Json(HttpStatusCode.Created, """
                    { "id": 21, "name": "robot$build", "secret": "build-secret", "expires_at": -1 }
                    """)
                : Json(HttpStatusCode.InternalServerError, """{ "message": "build-secret leaked upstream" }""");
        });
        var client = CreateClient(handler);

        var result = await client.ProvisionProjectAsync(new RegistryProjectProvisioningRequest("tenant", "pole-test"));

        Assert.False(result.TryGetValue(out _, out var failure));
        Assert.True(deletedRobot);
        Assert.DoesNotContain("build-secret", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArtifactLookup_MapsDigestAndSize()
    {
        var handler = new DelegateHandler(request => Task.FromResult(Json(
            HttpStatusCode.OK,
            """{ "digest": "sha256:abc", "size": 1234 }""")));
        var client = CreateClient(handler);

        var result = await client.GetArtifactAsync("pole-test", "service/api", "build-42");

        Assert.True(result.TryGetValue(out var artifact, out _));
        Assert.Equal("sha256:abc", artifact.Digest);
        Assert.Equal(1234, artifact.SizeBytes);
    }

    [Fact]
    public async Task NetworkFailure_DoesNotExposeTransportMessage()
    {
        var client = CreateClient(new ThrowingHandler());

        var result = await client.GetArtifactAsync("pole-test", "service", "tag");

        Assert.False(result.TryGetValue(out _, out var failure));
        Assert.DoesNotContain("admin-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.Contains("Network request failed", failure.ToString(), StringComparison.Ordinal);
    }

    private static HarborContainerRegistryClient CreateClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new HarborOptions
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
