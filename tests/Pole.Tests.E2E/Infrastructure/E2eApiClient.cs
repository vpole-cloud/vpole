using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Pole.Testing;

namespace Pole.Tests.E2E.Infrastructure;

public sealed class E2eApiClient(
    HttpClient client,
    TimeProvider? timeProvider = null
) : IDisposable
{
    private static readonly TimeSpan RenewBeforeExpiry = TimeSpan.FromSeconds(30);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, RegisteredToken> _tokens = new(StringComparer.Ordinal);

    public void Dispose()
    {
        client.Dispose();
        foreach (var registration in _tokens.Values)
            registration.Dispose();
        _tokens.Clear();
    }

    public void RegisterToken(string initialToken, Func<CancellationToken, Task<string>> renewToken)
    {
        var registration = new RegisteredToken(initialToken, renewToken);
        if (!_tokens.TryAdd(initialToken, registration))
        {
            registration.Dispose();
            throw new InvalidOperationException("The E2E bearer token is already registered.");
        }
    }

    public async Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        return await SendAsync(request, ct);
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default) =>
        SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completion,
        CancellationToken ct = default)
    {
        var authorization = request.Headers.Authorization;
        if (authorization is { Parameter: not null } &&
            string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
            _tokens.TryGetValue(authorization.Parameter, out var registration))
        {
            // Fixture users retain their original token as a stable lookup key. Renew before sending
            // after long suites or host suspension, without retrying API authorization failures.
            var token = await registration.GetCurrentTokenAsync(_time, ct);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var journal = TestPhaseJournal.Current;
        var requestId = journal?.AllocateRequestId() ?? 0;
        if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Put ||
            request.Method == HttpMethod.Patch || request.Method == HttpMethod.Delete)
            PhaseRequestEvidence.Record(journal, TestPhase.Action, request.RequestUri, requestId);
        PhaseRequestEvidence.Record(journal, TestPhase.RequestStarted, request.RequestUri, requestId);
        try
        {
            var response = await client.SendAsync(request, completion, ct);
            PhaseRequestEvidence.Record(
                journal,
                TestPhase.ResponseReceived,
                request.RequestUri,
                requestId,
                (int)response.StatusCode);
            return response;
        }
        catch (OperationCanceledException)
        {
            PhaseRequestEvidence.Record(journal, TestPhase.RequestCancelled, request.RequestUri, requestId);
            throw;
        }
        catch (HttpRequestException)
        {
            PhaseRequestEvidence.Record(journal, TestPhase.RequestFailed, request.RequestUri, requestId);
            throw;
        }
    }

    private sealed class RegisteredToken : IDisposable
    {
        private readonly Func<CancellationToken, Task<string>> _renewToken;
        private readonly SemaphoreSlim _renewal = new(1, 1);
        private readonly string _subject;
        private string _currentToken;
        private DateTimeOffset _expiresAt;

        public RegisteredToken(string token, Func<CancellationToken, Task<string>> renewToken)
        {
            var claims = ReadClaims(token);
            _currentToken = token;
            _subject = claims.Subject;
            _expiresAt = claims.ExpiresAt;
            _renewToken = renewToken;
        }

        public void Dispose() => _renewal.Dispose();

        public async Task<string> GetCurrentTokenAsync(TimeProvider time, CancellationToken ct)
        {
            await _renewal.WaitAsync(ct);
            try
            {
                if (_expiresAt > time.GetUtcNow() + RenewBeforeExpiry)
                    return _currentToken;

                var token = await _renewToken(ct);
                var claims = ReadClaims(token);
                if (!string.Equals(claims.Subject, _subject, StringComparison.Ordinal))
                    throw new InvalidOperationException("E2E token renewal changed the authenticated user.");
                if (claims.ExpiresAt <= time.GetUtcNow() + RenewBeforeExpiry)
                    throw new InvalidOperationException(
                        "The renewed E2E token is already near expiry; check the Keycloak clock.");

                _currentToken = token;
                _expiresAt = claims.ExpiresAt;
                return token;
            }
            finally
            {
                _renewal.Release();
            }
        }

        private static TokenClaims ReadClaims(string token)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length != 3)
                    throw new FormatException();
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                using var claims = JsonDocument.Parse(
                    Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
                var subject = claims.RootElement.GetProperty("sub").GetString();
                if (string.IsNullOrWhiteSpace(subject))
                    throw new FormatException();
                return new TokenClaims(
                    subject,
                    DateTimeOffset.FromUnixTimeSeconds(claims.RootElement.GetProperty("exp").GetInt64()));
            }
            catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException
                                                  or KeyNotFoundException or ArgumentOutOfRangeException)
            {
                // Never include a raw token or decoded claims in test failure output.
                throw new InvalidOperationException(
                    "The registered E2E bearer token has invalid subject or expiry claims.");
            }
        }

        private sealed record TokenClaims(
            string Subject,
            DateTimeOffset ExpiresAt
        );
    }
}
