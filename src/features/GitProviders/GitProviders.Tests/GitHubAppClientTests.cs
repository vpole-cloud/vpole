// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.GitHub;
using Xunit;

namespace Pole.Features.GitProviders.Tests;

public sealed class GitHubAppClientTests
{
    [Fact]
    public async Task Repositories_ArePagedAndInstallationTokenIsRefreshedNearExpiry()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var tokenRequests = 0;
        var handler = new DelegateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/app/installations/42/access_tokens")
            {
                tokenRequests++;
                return Json(
                    HttpStatusCode.Created,
                    $$"""
                      { "token": "installation-token-{{tokenRequests}}", "expires_at": "{{clock.GetUtcNow().AddHours(1):O}}" }
                      """);
            }

            Assert.Equal("/installation/repositories", path);
            Assert.Contains("page=2", request.RequestUri.Query);
            Assert.Contains("per_page=2", request.RequestUri.Query);
            return Json(
                HttpStatusCode.OK,
                """
                {
                  "total_count": 5,
                  "repositories": [
                    { "id": 1, "name": "one", "full_name": "org/one", "html_url": "https://github.com/org/one", "private": true, "default_branch": "main", "updated_at": "2026-07-10T10:00:00Z" },
                    { "id": 2, "name": "two", "full_name": "org/two", "html_url": "https://github.com/org/two", "private": false, "default_branch": "trunk", "updated_at": null }
                  ]
                }
                """);
        });
        var client = CreateClient(handler, clock);

        var first = await client.GetRepositoriesAsync("42", 2, 2);
        var cached = await client.GetRepositoriesAsync("42", 2, 2);

        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.Equal("org/two", first.Items[1].FullName);
        Assert.Equal(1, tokenRequests);
        Assert.Equal(first.Page, cached.Page);
        Assert.Equal(
            first.Items.Select(repository => repository.FullName),
            cached.Items.Select(repository => repository.FullName));

        clock.Advance(TimeSpan.FromMinutes(56));
        await client.GetRepositoriesAsync("42", 2, 2);
        Assert.Equal(2, tokenRequests);
    }

    [Fact]
    public async Task UserInstallationAccess_ReturnsFalseForForbidden()
    {
        var handler = new DelegateHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("short-lived-user-token", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        });
        var client = CreateClient(handler);

        var canAccess = await client.UserCanAccessInstallationAsync("short-lived-user-token", "42");

        Assert.False(canAccess);
    }

    [Fact]
    public async Task RateLimitAndNetworkFailures_AreReturnedWithoutTokenMaterial()
    {
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal))
                return Json(
                    HttpStatusCode.Created,
                    """
                    { "token": "sensitive-installation-token", "expires_at": "2026-07-10T13:00:00Z" }
                    """);

            return Json(
                HttpStatusCode.TooManyRequests,
                "{ \"message\": \"sensitive-installation-token rate limited\" }");
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetRepositoriesAsync("42", 1, 20));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.DoesNotContain("sensitive-installation-token", exception.ToString(), StringComparison.Ordinal);

        var networkClient = CreateClient(new ThrowingHandler());
        var networkException = await Assert.ThrowsAsync<GitHubApiException>(() =>
            networkClient.GetInstallationAsync("42"));
        Assert.Null(networkException.StatusCode);
        Assert.Equal("GitHub could not be reached.", networkException.Message);
    }

    [Fact]
    public async Task OAuthFailure_DoesNotExposeCodeClientSecretOrResponseBody()
    {
        var handler = new DelegateHandler(_ => Json(
            HttpStatusCode.InternalServerError,
            "{ \"message\": \"oauth-code test-client-secret verifier\" }"));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GitHubApiException>(() =>
            client.ExchangeOAuthCodeAsync("oauth-code", "verifier"));
        var rendered = exception.ToString();

        Assert.DoesNotContain("oauth-code", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("test-client-secret", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("verifier", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchiveDownload_UsesExactCommitAndInstallationToken()
    {
        var handler = new DelegateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/access_tokens", StringComparison.Ordinal))
                return Json(
                    HttpStatusCode.Created,
                    """
                    { "token": "installation-token", "expires_at": "2026-07-10T13:00:00Z" }
                    """);
            if (path == "/repositories/9001")
                return Json(
                    HttpStatusCode.OK,
                    """
                    { "id": 9001, "name": "sample", "full_name": "lsbV/sample", "html_url": "https://github.com/lsbV/sample", "private": true, "default_branch": "main", "updated_at": null }
                    """);

            Assert.Equal("/repos/lsbV/sample/tarball/abc123", path);
            Assert.Equal("installation-token", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4]),
            };
        });
        var client = CreateClient(handler);
        await using var destination = new MemoryStream();

        await client.DownloadRepositoryArchiveAsync("42", "9001", "abc123", destination);

        Assert.Equal([1, 2, 3, 4], destination.ToArray());
    }

    [Fact]
    public async Task AnalysisContentsUseResolvedCommitWhenBranchMoves()
    {
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var client = CreateClient(
            new DelegateHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/access_tokens", StringComparison.Ordinal))
                    return Token();
                if (path == "/repositories/9001")
                    return Repository();
                if (path.Contains("/branches/", StringComparison.Ordinal))
                {
                    Assert.Contains("feature%2Fmanifests", request.RequestUri.OriginalString);
                    return Json(HttpStatusCode.OK, $$$"""{"name":"feature/manifests","commit":{"sha":"{{{sha}}}"}}""");
                }

                if (path.Contains("/git/commits/", StringComparison.Ordinal))
                {
                    Assert.EndsWith(sha, path);
                    return Json(HttpStatusCode.OK, """{"tree":{"sha":"tree"}}""");
                }

                if (path.Contains("/git/trees/", StringComparison.Ordinal))
                    return Json(
                        HttpStatusCode.OK,
                        """{"tree":[{"path":"Dockerfile","type":"blob","mode":"100644"}],"truncated":false}""");

                Assert.Equal($"?ref={sha}", request.RequestUri.Query);
                return Json(HttpStatusCode.OK, """{"encoding":"base64","content":"RlJPTSBhbHBpbmU="}""");
            }));

        var snapshot = await client.GetRepositorySnapshotAsync("42", "9001", "feature/manifests");
        Assert.Equal(sha, snapshot.CommitSha);
        Assert.Equal("FROM alpine", snapshot.FileContents["Dockerfile"]);
    }

    [Fact]
    public async Task ArchiveRedirectNeverForwardsInstallationTokenAcrossOrigins()
    {
        var client = CreateClient(
            new DelegateHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal))
                    return Token();
                if (request.RequestUri.AbsolutePath == "/repositories/9001")
                    return Repository();
                if (request.RequestUri.Host == "api.test")
                {
                    Assert.Equal("installation-token", request.Headers.Authorization?.Parameter);
                    return Redirect("https://codeload.github.com/org/sample/tar.gz/sha?temporary=private");
                }

                Assert.Null(request.Headers.Authorization);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            }));
        using var destination = new MemoryStream();
        await client.DownloadRepositoryArchiveAsync("42", "9001", "sha", destination);
        Assert.Equal([1, 2, 3], destination.ToArray());
    }

    [Theory]
    [InlineData("https://untrusted.test/archive")]
    [InlineData("http://codeload.github.com/archive")]
    [InlineData("https://user:password@codeload.github.com/archive")]
    [InlineData("https://api.test/loop")]
    public async Task UnsafeAndLoopingArchiveRedirectsAreRejected(string location)
    {
        var redirects = 0;
        var client = CreateClient(
            new DelegateHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal))
                    return Token();
                if (request.RequestUri.AbsolutePath == "/repositories/9001")
                    return Repository();

                redirects++;
                return Redirect(location);
            }));
        using var destination = new MemoryStream();
        var error = await Assert.ThrowsAsync<GitHubUnsafeRedirectException>(() =>
            client.DownloadRepositoryArchiveAsync("42", "9001", "sha", destination));
        Assert.InRange(redirects, 1, 4);
        Assert.DoesNotContain(location, error.ToString());
        Assert.Empty(destination.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForbiddenRateLimitsRemainDistinguishableFromAccessDenial(bool rateLimit)
    {
        var client = CreateClient(
            new DelegateHandler(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal)
                    ? Token()
                    : Json(
                        HttpStatusCode.Forbidden,
                        rateLimit
                            ? "{\"message\":\"You have exceeded a secondary rate limit.\"}"
                            : "{\"message\":\"Permission denied.\"}")));
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetRepositoryAsync("42", "9001"));
        Assert.Equal(rateLimit, error.IsRateLimited);
        Assert.Equal(GitHubRequestTarget.Repository, error.Target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchiveBodyDeadlineCoversStallsAndPreservesCallerCancellation(bool callerCancels)
    {
        var content = new StallingContent();
        var client = CreateClient(
            new DelegateHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal))
                    return Token();
                if (request.RequestUri.AbsolutePath == "/repositories/9001")
                    return Repository();

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }),
            timeout: TimeSpan.FromMilliseconds(500));
        using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        var download = client.DownloadRepositoryArchiveAsync("42", "9001", "sha", destination, cancellation.Token);
        await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancels)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            await Assert.ThrowsAsync<GitHubApiException>(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    private static HttpResponseMessage Token() => Json(
        HttpStatusCode.Created,
        """{"token":"installation-token","expires_at":"2099-01-01T00:00:00Z"}""");

    private static HttpResponseMessage Repository() => Json(
        HttpStatusCode.OK,
        """
        {"id":9001,"name":"sample","full_name":"org/sample","html_url":"https://github.com/org/sample",
        "private":true,"default_branch":"main","updated_at":null}
        """);

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static GitHubAppClient CreateClient(
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        TimeSpan? timeout = null)
    {
        var options = new GitHubAppOptions
        {
            Enabled = true,
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret",
            AppSlug = "pole-test",
            PrivateKeyPath = "not-used.pem",
            PublicBaseUrl = "https://pole.test",
            ApiBaseUrl = "https://api.test",
            WebBaseUrl = "https://github.test",
        };
        return new GitHubAppClient(
            new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(100) },
            options,
            () => "app-jwt",
            new MemoryCache(new MemoryCacheOptions()),
            timeProvider ?? TimeProvider.System);
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StallingContent : HttpContent
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class MutableTimeProvider(
        DateTimeOffset utcNow
    ) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("network details containing a credential");
    }
}
