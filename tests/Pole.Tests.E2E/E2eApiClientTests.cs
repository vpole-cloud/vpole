using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class E2eApiClientTests
{
    [Fact]
    public async Task Overnight_clock_gap_renews_the_same_user_before_sending_and_preserves_the_request()
    {
        var clock = new TestClock();
        var initial = Token("alpha", clock.GetUtcNow().AddHours(1));
        clock.Advance(TimeSpan.FromHours(12));
        var renewed = Token("alpha", clock.GetUtcNow().AddHours(1));
        var renewals = 0;
        var sends = 0;
        using var api = Client(
            clock,
            async (request, ct) =>
            {
                sends++;
                Assert.Equal(renewed, request.Headers.Authorization?.Parameter);
                Assert.Equal("tenant-alpha", Assert.Single(request.Headers.GetValues("X-Pole-Tenant-ID")));
                Assert.Equal("/operation", request.RequestUri?.AbsolutePath);
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("unchanged body", await request.Content!.ReadAsStringAsync(ct));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });
        api.RegisterToken(
            initial,
            _ =>
            {
                renewals++;
                return Task.FromResult(renewed);
            });
        using var request = Authenticated(initial, HttpMethod.Post);
        request.Headers.Add("X-Pole-Tenant-ID", "tenant-alpha");
        request.Content = new StringContent("unchanged body");

        using var response = await api.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, renewals);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Concurrent_requests_share_one_renewal_and_subsequent_requests_use_the_cached_token()
    {
        var clock = new TestClock();
        var initial = Token("alpha", clock.GetUtcNow().AddSeconds(20));
        var renewed = Token("alpha", clock.GetUtcNow().AddHours(1));
        var renewals = 0;
        var sends = 0;
        using var api = Client(
            clock,
            (request, _) =>
            {
                Interlocked.Increment(ref sends);
                Assert.Equal(renewed, request.Headers.Authorization?.Parameter);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            });
        api.RegisterToken(
            initial,
            async _ =>
            {
                Interlocked.Increment(ref renewals);
                await Task.Yield();
                return renewed;
            });

        await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(async _ =>
                {
                    using var request = Authenticated(initial);
                    using var response = await api.SendAsync(request);
                    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                }));
        using var subsequent = Authenticated(initial);
        using var subsequentResponse = await api.SendAsync(subsequent);

        Assert.Equal(1, renewals);
        Assert.Equal(9, sends);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Authorization_failures_are_returned_without_renewal_or_API_retries(HttpStatusCode status)
    {
        var clock = new TestClock();
        var initial = Token("alpha", clock.GetUtcNow().AddHours(1));
        var sends = 0;
        using var api = Client(
            clock,
            (request, _) =>
            {
                sends++;
                Assert.Equal(initial, request.Headers.Authorization?.Parameter);
                return Task.FromResult(new HttpResponseMessage(status));
            });
        api.RegisterToken(initial, _ => throw new InvalidOperationException("Unexpected renewal."));
        using var request = Authenticated(initial);

        using var response = await api.SendAsync(request);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Anonymous_unregistered_and_non_bearer_requests_keep_their_original_authorization()
    {
        var clock = new TestClock();
        var expired = Token("alpha", clock.GetUtcNow().AddHours(-1));
        var seen = new List<AuthenticationHeaderValue?>();
        using var api = Client(
            clock,
            (request, _) =>
            {
                seen.Add(request.Headers.Authorization);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            });
        api.RegisterToken(expired, _ => throw new InvalidOperationException("Unexpected renewal."));
        using var anonymous = await api.GetAsync("/operation");
        using var unknown = Authenticated("unregistered-token");
        using var unknownResponse = await api.SendAsync(unknown);
        using var otherScheme = new HttpRequestMessage(HttpMethod.Get, "/operation");
        otherScheme.Headers.Authorization = new AuthenticationHeaderValue("Basic", expired);
        using var otherResponse = await api.SendAsync(otherScheme);

        Assert.Null(seen[0]);
        Assert.Equal("unregistered-token", seen[1]?.Parameter);
        Assert.Equal("Basic", seen[2]?.Scheme);
        Assert.Equal(expired, seen[2]?.Parameter);
        Assert.Equal(3, seen.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Renewal_rejects_a_different_user_or_still_expired_token_before_sending(bool differentUser)
    {
        var clock = new TestClock();
        var initial = Token("alpha", clock.GetUtcNow().AddHours(-1));
        var renewed = differentUser
            ? Token("beta", clock.GetUtcNow().AddHours(1))
            : Token("alpha", clock.GetUtcNow().AddHours(-1));
        using var api = Client(clock, (_, _) => throw new InvalidOperationException("Unexpected API request."));
        api.RegisterToken(initial, _ => Task.FromResult(renewed));
        using var request = Authenticated(initial);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => api.SendAsync(request));

        Assert.DoesNotContain(initial, error.Message);
        Assert.DoesNotContain(renewed, error.Message);
        Assert.Contains(differentUser ? "changed the authenticated user" : "near expiry", error.Message);
    }

    [Fact]
    public async Task Cancellation_during_renewal_does_not_send_a_request()
    {
        var clock = new TestClock();
        var initial = Token("alpha", clock.GetUtcNow().AddHours(-1));
        using var cancellation = new CancellationTokenSource();
        using var api = Client(clock, (_, _) => throw new InvalidOperationException("Unexpected API request."));
        api.RegisterToken(
            initial,
            async ct =>
            {
                await cancellation.CancelAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return initial;
            });
        using var request = Authenticated(initial);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.SendAsync(request, cancellation.Token));
    }

    [Fact]
    public async Task Response_headers_read_preserves_streaming_without_buffering_the_content()
    {
        var clock = new TestClock();
        using var api = Client(
            clock,
            (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new UnbufferedContent(),
                }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/operation");

        using var response = await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static E2eApiClient Client(
        TimeProvider clock,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(
        new HttpClient(new Handler(send)) { BaseAddress = new Uri("http://e2e.invalid") },
        clock);

    private static HttpRequestMessage Authenticated(string token, HttpMethod? method = null) =>
        new(method ?? HttpMethod.Get, "/operation")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
        };

    private static string Token(string subject, DateTimeOffset expires)
    {
        // Synthetic unsigned tokens are only read by the fixture helper, never accepted by Pole.
        var claims = JsonSerializer.SerializeToUtf8Bytes(new { sub = subject, exp = expires.ToUnixTimeSeconds() });
        var payload = Convert.ToBase64String(claims).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"e30.{payload}.unsigned";
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 5, 20, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class UnbufferedContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Streaming response was buffered.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
