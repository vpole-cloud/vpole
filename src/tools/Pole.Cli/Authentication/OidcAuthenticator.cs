using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pole.Cli.Authentication;

public interface IOidcBrowser
{
    void Open(Uri uri);
}

public sealed record OidcTokens(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() => $"OidcTokens {{ AccessToken = [REDACTED], RefreshToken = [REDACTED], ExpiresAtUtc = {ExpiresAtUtc:O} }}";
}

public sealed class OidcAuthenticator(HttpClient httpClient, IOidcBrowser browser)
{
    public static readonly Uri RedirectUri = new("http://127.0.0.1:48763/callback");
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(4);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    public async Task<OidcTokens> LoginAsync(Uri issuer, string clientId, CancellationToken cancellationToken)
    {
        ValidateIssuer(issuer);
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("OIDC client ID is required.", nameof(clientId));

        var metadataUri = new Uri(issuer.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration");
        using var metadataResponse = await httpClient.GetAsync(metadataUri, cancellationToken);
        if (!metadataResponse.IsSuccessStatusCode)
            throw new InvalidOperationException("Could not read the OIDC discovery document.");
        var metadata = await metadataResponse.Content.ReadFromJsonAsync<OidcMetadata>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The OIDC discovery document was empty.");
        if (!Uri.TryCreate(metadata.Issuer, UriKind.Absolute, out var discoveredIssuer) ||
            !SameIssuer(issuer, discoveredIssuer))
            throw new InvalidOperationException("The OIDC discovery document issuer does not match the configured issuer.");

        var authorizationEndpoint = ValidateEndpoint(metadata.AuthorizationEndpoint, issuer, "authorization");
        var tokenEndpoint = ValidateEndpoint(metadata.TokenEndpoint, issuer, "token");
        var state = RandomBase64Url(32);
        var verifier = RandomBase64Url(32);
        var challenge = CreateCodeChallenge(verifier);

        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:48763/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException error)
        {
            throw new InvalidOperationException("Could not bind the OIDC callback at 127.0.0.1:48763. Close the process using that port and try again.", error);
        }

        var authorizationUri = BuildAuthorizationUri(
            authorizationEndpoint,
            clientId,
            RedirectUri,
            state,
            challenge);
        browser.Open(authorizationUri);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallbackTimeout);
        var authorizationCode = await WaitForAuthorizationCodeAsync(listener, state, timeout.Token);

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = authorizationCode,
                ["redirect_uri"] = RedirectUri.AbsoluteUri,
                ["code_verifier"] = verifier
            })
        };
        using var tokenResponse = await httpClient.SendAsync(request, cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
            throw new InvalidOperationException("The identity provider rejected the authorization code exchange.");
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The OIDC token response was empty.");
        if (!string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(token.AccessToken))
            throw new InvalidOperationException("The OIDC token response did not contain a bearer access token.");
        if (token.ExpiresIn is < 1 or > 86400)
            throw new InvalidOperationException("The OIDC token response contained an invalid expiry.");

        return new OidcTokens(token.AccessToken, token.RefreshToken, DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn));
    }

    public async Task<OidcTokens?> RefreshAsync(
        Uri issuer,
        string clientId,
        OidcTokens current,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(current.RefreshToken))
            return null;

        ValidateIssuer(issuer);
        var metadataUri = new Uri(issuer.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration");
        using var metadataResponse = await httpClient.GetAsync(metadataUri, cancellationToken);
        if (!metadataResponse.IsSuccessStatusCode)
            throw new InvalidOperationException("Could not read the OIDC discovery document.");
        var metadata = await metadataResponse.Content.ReadFromJsonAsync<OidcMetadata>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The OIDC discovery document was empty.");
        if (!Uri.TryCreate(metadata.Issuer, UriKind.Absolute, out var discoveredIssuer) ||
            !SameIssuer(issuer, discoveredIssuer))
            throw new InvalidOperationException("The OIDC discovery document issuer does not match the configured issuer.");

        var tokenEndpoint = ValidateEndpoint(metadata.TokenEndpoint, issuer, "token");
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = current.RefreshToken
            })
        };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("The identity provider rejected the token refresh.");

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The OIDC token response was empty.");
        if (!string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresIn is < 1 or > 86400)
            throw new InvalidOperationException("The OIDC token response was invalid.");

        return new OidcTokens(
            token.AccessToken,
            token.RefreshToken ?? current.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn));
    }

    internal static Uri BuildAuthorizationUri(
        Uri endpoint,
        string clientId,
        Uri redirectUri,
        string state,
        string codeChallenge)
    {
        var builder = new UriBuilder(endpoint);
        var existing = builder.Query.TrimStart('?');
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = "openid profile email",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        var query = string.Join('&', parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        builder.Query = string.IsNullOrEmpty(existing) ? query : $"{existing}&{query}";
        return builder.Uri;
    }

    internal static string CreateCodeChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static bool HasDuplicate(System.Collections.Specialized.NameValueCollection query, string key) =>
        query.GetValues(key) is { Length: > 1 };

    internal static async Task<string> WaitForAuthorizationCodeAsync(
        HttpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            using (context.Response)
            {
                if (context.Request.RemoteEndPoint is not { Address: var address } || !IPAddress.IsLoopback(address) ||
                    context.Request.HttpMethod != "GET" || context.Request.Url?.AbsolutePath != "/callback")
                {
                    await RespondAsync(context.Response, HttpStatusCode.BadRequest, "Invalid login callback. Return to the CLI and retry.");
                    continue;
                }

                if (context.Request.Url?.Query.Length > 8192 ||
                    HasDuplicate(context.Request.QueryString, "state") ||
                    HasDuplicate(context.Request.QueryString, "code") ||
                    HasDuplicate(context.Request.QueryString, "error"))
                {
                    await RespondAsync(context.Response, HttpStatusCode.BadRequest, "Invalid login response. Return to the CLI and retry.");
                    continue;
                }

                var suppliedState = context.Request.QueryString["state"];
                if (suppliedState is null || suppliedState.Length > 256 || !FixedTimeEquals(expectedState, suppliedState))
                {
                    await RespondAsync(context.Response, HttpStatusCode.BadRequest, "Invalid login response. Return to the CLI and retry.");
                    continue;
                }

                var oauthError = context.Request.QueryString["error"];
                if (oauthError is not null)
                {
                    await RespondAsync(context.Response, HttpStatusCode.BadRequest, "Login was denied. You may close this window.");
                    throw new InvalidOperationException("The identity provider denied the login request.");
                }

                var code = context.Request.QueryString["code"];
                if (string.IsNullOrWhiteSpace(code) || code.Length > 8192)
                {
                    await RespondAsync(context.Response, HttpStatusCode.BadRequest, "Invalid login response. Return to the CLI and retry.");
                    continue;
                }

                await RespondAsync(context.Response, HttpStatusCode.OK, "Login complete. You may close this window and return to the CLI.");
                return code;
            }
        }
    }

    internal static bool IsSecureEndpoint(Uri endpoint, Uri issuer) =>
        endpoint.Scheme == Uri.UriSchemeHttps ||
        endpoint.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(endpoint.Host) && IsLoopbackHost(issuer.Host);

    internal static void ValidateDiscoveredIssuer(Uri issuer) => ValidateIssuer(issuer);

    private static Uri ValidateEndpoint(string value, Uri issuer, string description)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || !IsSecureEndpoint(endpoint, issuer))
            throw new InvalidOperationException($"The OIDC discovery {description} endpoint must use HTTPS outside loopback development.");
        return endpoint;
    }

    private static void ValidateIssuer(Uri issuer)
    {
        if (issuer.Scheme is not ("https" or "http") || issuer.Query.Length != 0 || issuer.Fragment.Length != 0 ||
            issuer.Scheme == "http" && !IsLoopbackHost(issuer.Host))
            throw new InvalidOperationException("OIDC issuer must use HTTPS; HTTP is allowed only for a loopback development issuer.");
    }

    private static bool SameIssuer(Uri configured, Uri discovered) =>
        string.Equals(configured.AbsoluteUri.TrimEnd('/'), discovered.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal);

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static async Task RespondAsync(HttpListenerResponse response, HttpStatusCode status, string message)
    {
        var payload = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>Pole login</title><p>{WebUtility.HtmlEncode(message)}</p>");
        response.StatusCode = (int)status;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = payload.Length;
        response.Headers[HttpResponseHeader.CacheControl] = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        await response.OutputStream.WriteAsync(payload);
    }

    private static string RandomBase64Url(int length) => Base64Url(RandomNumberGenerator.GetBytes(length));

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record OidcMetadata(
        [property: JsonPropertyName("issuer")] string Issuer,
        [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string TokenEndpoint);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType);
}
