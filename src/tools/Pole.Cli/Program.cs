using Pole.Cli.Api;
using Pole.Cli.Authentication;
using Pole.Cli.Forwarding;
using System.Net.Sockets;

if (args.Length == 0 || args[0] is not ("login" or "forward"))
{
    Console.Error.WriteLine("Usage: pole forward --server <url> --issuer <url> --client-id <id> --tenant <id> --grant <id> --port <port>");
    return 2;
}

var options = CliOptions.Parse(args[1..], args[0] == "forward");
if (options is null)
    return 2;

using var httpClient = OidcAuthenticator.CreateHttpClient();
var authenticator = new OidcAuthenticator(httpClient, new SystemBrowser());
var tokenProvider = new OidcAccessTokenProvider(options.Issuer, options.ClientId, options.Server, authenticator, new CliTokenStore());
var api = new PrivateAccessApiClient(httpClient, options.Server, tokenProvider.GetAccessTokenAsync);
var userCancelled = 0;

try
{
    CliTokenStore.ValidateServer(options.Server);
    if (args[0] == "forward")
        _ = WebSocketPrivateAccessStreamTransport.GetEndpoint(options.Server);
    if (args[0] == "login")
    {
        _ = await tokenProvider.GetAccessTokenAsync(CancellationToken.None);
        Console.WriteLine("Signed in. Tokens were saved to the current user's private data directory.");
        return 0;
    }

    var grants = await api.GetGrantsAsync(options.TenantId!, CancellationToken.None);
    var grant = grants.SingleOrDefault(candidate => candidate.Id == options.GrantId && candidate.TenantId == options.TenantId);
    if (grant is null || grant.Revoked || grant.ExpiresAtUtc <= DateTimeOffset.UtcNow)
    {
        Console.Error.WriteLine("The selected Private Access grant is unavailable, revoked, expired, or outside this tenant.");
        return 1;
    }

    Console.WriteLine($"Grant {DisplayValue(grant.Id)}: {DisplayValue(grant.ResourceType)} {DisplayValue(grant.ResourceId)}, deployment {DisplayValue(grant.ResourceDeploymentId)}, endpoint {DisplayValue(grant.EndpointId)}; expires {grant.ExpiresAtUtc:O}.");
    var connectionFactory = new AuthorizedPrivateAccessConnectionFactory(
        options.Server,
        api,
        new WebSocketPrivateAccessStreamTransport());
    var forwarder = new LocalForwarder(options.TenantId!, options.GrantId!, connectionFactory);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += CancelForward;
    try
    {
        await forwarder.RunAsync(options.Port, cancellation.Token, endpoint =>
            Console.WriteLine($"Listening on {endpoint.Address}:{endpoint.Port}. Press Ctrl+C to stop."));
    }
    finally
    {
        Console.CancelKeyPress -= CancelForward;
    }
    return 0;

    void CancelForward(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        Interlocked.Exchange(ref userCancelled, 1);
        cancellation.Cancel();
    }

    static string DisplayValue(string value)
    {
        const int maximumLength = 96;
        var sanitized = new string(value.Take(maximumLength).Select(character => char.IsControl(character) ? '?' : character).ToArray());
        return value.Length > maximumLength ? sanitized + "…" : sanitized;
    }
}
catch (OperationCanceledException) when (Volatile.Read(ref userCancelled) != 0)
{
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("The operation timed out or was cancelled before it completed.");
    return 1;
}
catch (SocketException)
{
    Console.Error.WriteLine("Could not bind or use the selected loopback port. Choose an available port and retry.");
    return 1;
}
catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or System.Net.WebSockets.WebSocketException)
{
    Console.Error.WriteLine("Pole Private Access could not start. Check the server, identity settings and selected grant, then retry.");
    return 1;
}

internal sealed record CliOptions(
    Uri Issuer,
    string ClientId,
    Uri Server,
    string? TenantId,
    string? GrantId,
    int Port)
{
    public static CliOptions? Parse(string[] arguments, bool requireGrant)
    {
        string? issuerText = null;
        string? clientId = null;
        string? serverText = null;
        string? tenantId = null;
        string? grantId = null;
        string? portText = null;

        for (var index = 0; index < arguments.Length; index++)
        {
            if (index + 1 >= arguments.Length)
            {
                Console.Error.WriteLine($"Missing value for '{arguments[index]}'.");
                return null;
            }

            var value = arguments[++index];
            switch (arguments[index - 1])
            {
                case "--issuer":
                    issuerText = value;
                    break;
                case "--client-id":
                    clientId = value;
                    break;
                case "--server":
                    serverText = value;
                    break;
                case "--tenant":
                    tenantId = value;
                    break;
                case "--grant":
                    grantId = value;
                    break;
                case "--port":
                    portText = value;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown option '{arguments[index - 1]}'.");
                    return null;
            }
        }

        if (!Uri.TryCreate(issuerText, UriKind.Absolute, out var issuer) ||
            !Uri.TryCreate(serverText, UriKind.Absolute, out var server) ||
            string.IsNullOrWhiteSpace(clientId) ||
            requireGrant && (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(grantId)) ||
            requireGrant && (!int.TryParse(portText, out var parsedPort) || parsedPort is < 1 or > 65535))
        {
            Console.Error.WriteLine("Issuer, client ID and server are required; forward also requires tenant, grant and a port from 1 to 65535.");
            return null;
        }

        return new CliOptions(
            issuer,
            clientId,
            server,
            tenantId,
            grantId,
            int.TryParse(portText, out var port) ? port : 0);
    }
}

internal sealed class SystemBrowser : IOidcBrowser
{
    public void Open(Uri uri)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        };
        System.Diagnostics.Process.Start(startInfo);
    }
}
