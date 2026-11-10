using System.ComponentModel;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security;
using System.Text.Json;
using Pole.Cli.Api;
using Pole.Cli.Authentication;
using Pole.Cli.Forwarding;

namespace Pole.Cli;

public sealed class CliCommandRunner(
    HttpClient httpClient,
    CliTokenStore tokenStore,
    OidcAuthenticator authenticator
)
{
    public async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.Length == 0)
            return Usage();

        try
        {
            return arguments[0] switch
            {
                "login" => await LoginAsync(arguments[1..], cancellationToken),
                "status" when arguments.Length == 1 => await StatusAsync(cancellationToken),
                "tenant" => await TenantAsync(arguments[1..], cancellationToken),
                "forward" => await ForwardAsync(arguments[1..], cancellationToken),
                "logout" when arguments.Length == 1 => Logout(),
                _ => Usage(),
            };
        }
        catch (CliLoginRequiredException)
        {
            Console.Error.WriteLine("Sign-in is required. Run 'pole login' and retry.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("The operation timed out or was cancelled before it completed.");
            return 1;
        }
        catch (SocketException)
        {
            Console.Error.WriteLine(
                "Could not bind or use the selected loopback port. Choose an available port and retry.");
            return 1;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException
                                          or ArgumentException or FormatException or TimeoutException
                                          or UnauthorizedAccessException or SecurityException or Win32Exception
                                          or JsonException or WebSocketException)
        {
            Console.Error.WriteLine("Pole CLI command failed. Check the server and your access, then retry.");
            return 1;
        }
    }

    private async Task<int> LoginAsync(string[] arguments, CancellationToken cancellationToken)
    {
        if (!TryReadServerOption(arguments, out var requestedServer))
            return Usage();

        Uri server;
        PoleServerIdentity identity;
        if (requestedServer is not null)
        {
            server = requestedServer;
            identity = await new PoleServerDiscovery(httpClient).DiscoverAsync(server, cancellationToken);
            await tokenStore.SaveProfileAsync(server, identity, cancellationToken);
        }
        else
        {
            var saved = await tokenStore.LoadProfileAsync(cancellationToken);
            if (saved is null)
            {
                Console.Error.WriteLine("No Pole server is saved. Run 'pole login --server <url>' first.");
                return 2;
            }

            (server, identity) = saved.Value;
        }

        var provider = CreateTokenProvider(server, identity);
        var accessToken = await provider.GetAccessTokenAsync(cancellationToken);
        var accountKey = CliAccountIdentity.GetAccountKey(accessToken, identity.Issuer, server);
        var api = CreateApi(server, provider, allowInteractiveLogin: false);
        var tenants = await api.GetTenantsAsync(cancellationToken);
        var activeTenant = await tokenStore.GetActiveTenantAsync(accountKey, cancellationToken);
        if (activeTenant is not null && tenants.Any(tenant => tenant.Id == activeTenant))
        {
            Console.WriteLine(
                $"Signed in to {CliConsoleText.SafeValue(server.GetLeftPart(UriPartial.Authority))}. Active tenant: {CliConsoleText.SafeValue(activeTenant)}.");
            return 0;
        }

        if (tenants.Count == 1)
        {
            await tokenStore.SaveActiveTenantAsync(accountKey, tenants[0].Id, cancellationToken);
            Console.WriteLine(
                $"Signed in to {CliConsoleText.SafeValue(server.GetLeftPart(UriPartial.Authority))}. Selected tenant: {CliConsoleText.SafeValue(tenants[0].Id)}.");
            return 0;
        }

        if (tenants.Count == 0)
        {
            Console.WriteLine(
                $"Signed in to {CliConsoleText.SafeValue(server.GetLeftPart(UriPartial.Authority))}. No tenant memberships are available.");
            return 0;
        }

        Console.WriteLine("Select a tenant for this account:");
        var selected = PromptForTenant(tenants);
        if (selected is null)
        {
            Console.WriteLine("Sign-in complete. Run 'pole tenant select <tenant-id>' to choose a tenant.");
            return 0;
        }

        await tokenStore.SaveActiveTenantAsync(accountKey, selected.Id, cancellationToken);
        Console.WriteLine(
            $"Signed in to {CliConsoleText.SafeValue(server.GetLeftPart(UriPartial.Authority))}. Selected tenant: {CliConsoleText.SafeValue(selected.Id)}.");
        return 0;
    }

    private async Task<int> StatusAsync(CancellationToken cancellationToken)
    {
        var profile = await LoadProfileAsync(cancellationToken);
        if (profile is null)
        {
            Console.WriteLine("No Pole server is saved. Run 'pole login --server <url>' to get started.");
            return 0;
        }

        var (server, identity) = profile.Value;
        var provider = CreateTokenProvider(server, identity);
        try
        {
            var accessToken = await provider.GetAccessTokenAsync(cancellationToken, allowInteractiveLogin: false);
            var accountKey = CliAccountIdentity.GetAccountKey(accessToken, identity.Issuer, server);
            var tenants = await CreateApi(server, provider, allowInteractiveLogin: false)
                .GetTenantsAsync(cancellationToken);
            var activeTenant = await tokenStore.GetActiveTenantAsync(accountKey, cancellationToken);
            Console.WriteLine($"Server: {CliConsoleText.SafeValue(server.GetLeftPart(UriPartial.Authority))}");
            Console.WriteLine("Authentication: signed in");
            Console.WriteLine(
                $"Active tenant: {(activeTenant is not null && tenants.Any(tenant => tenant.Id == activeTenant) ? CliConsoleText.SafeValue(activeTenant) : "not selected")}");
            return 0;
        }
        catch (CliLoginRequiredException)
        {
            Console.WriteLine($"Server: {CliConsoleText.SafeValue(server.GetLeftPart(UriPartial.Authority))}");
            Console.WriteLine("Authentication: signed out; run 'pole login'");
            return 0;
        }
    }

    private async Task<int> TenantAsync(string[] arguments, CancellationToken cancellationToken)
    {
        if (arguments.Length == 1 && arguments[0] == "list")
        {
            var (_, _, _, accountKey, tenants) = await GetAuthenticatedTenantsAsync(cancellationToken);
            var selected = await tokenStore.GetActiveTenantAsync(accountKey, cancellationToken);
            foreach (var tenant in tenants)
                Console.WriteLine(
                    $"{(tenant.Id == selected ? "*" : " ")} {CliConsoleText.SafeValue(tenant.Id)}\t{CliConsoleText.SafeValue(tenant.Name)}\t{CliConsoleText.SafeValue(tenant.Role)}");
            if (tenants.Count == 0)
                Console.WriteLine("No tenant memberships are available.");
            return 0;
        }

        if (arguments.Length == 2 && arguments[0] == "select" && !string.IsNullOrWhiteSpace(arguments[1]))
        {
            var (_, _, _, accountKey, tenants) = await GetAuthenticatedTenantsAsync(cancellationToken);
            switch (TenantSelection.Resolve(arguments[1], tenants))
            {
                case TenantSelectionFound found:
                    await tokenStore.SaveActiveTenantAsync(accountKey, found.Tenant.Id, cancellationToken);
                    Console.WriteLine($"Active tenant: {CliConsoleText.SafeValue(found.Tenant.Id)}.");
                    return 0;
                case TenantSelectionAmbiguous ambiguous:
                    Console.Error.WriteLine(
                        $"More than one tenant is named '{CliConsoleText.SafeValue(arguments[1])}'. Choose its exact tenant ID:");
                    foreach (var match in ambiguous.Matches)
                        Console.Error.WriteLine($"- {CliConsoleText.SafeValue(match.Id)}");
                    return 1;
                default:
                    Console.Error.WriteLine(
                        $"Tenant '{CliConsoleText.SafeValue(arguments[1])}' is not available to this account by exact ID or name.");
                    return 1;
            }
        }

        return Usage();
    }

    private async Task<int> ForwardAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var separator = arguments.Length == 0 ? -1 : arguments[0].IndexOf('/');
        if (separator <= 0 || separator == arguments[0].Length - 1 || !TryReadForwardOptions(
                arguments[1..],
                out var deploymentId,
                out var endpointId,
                out var localPort))
            return Usage();
        var kind = arguments[0][..separator];
        var name = arguments[0][(separator + 1)..];
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(name))
            return Usage();

        var (server, _, provider, accountKey, tenants) = await GetAuthenticatedTenantsAsync(cancellationToken);
        var tenantId = await tokenStore.GetActiveTenantAsync(accountKey, cancellationToken);
        if (tenantId is null || tenants.All(tenant => tenant.Id != tenantId))
        {
            Console.Error.WriteLine("No tenant is selected. Run 'pole tenant select <tenant-id>' and retry.");
            return 1;
        }

        _ = WebSocketPrivateAccessStreamTransport.GetEndpoint(server);
        var api = CreateApi(server, provider, allowInteractiveLogin: false);
        var options = await api.GetResourceGrantsAsync(
            tenantId,
            kind,
            name,
            deploymentId,
            endpointId,
            cancellationToken);
        options = options.Where(option =>
                !string.IsNullOrWhiteSpace(option.GrantId) && !string.IsNullOrWhiteSpace(option.ResourceDeploymentId) &&
                !string.IsNullOrWhiteSpace(option.EndpointId) && option.ExpiresAtUtc > DateTimeOffset.UtcNow)
            .ToArray();
        if (options.Count == 0)
        {
            Console.Error.WriteLine(
                $"No eligible Private Access grant was found for {CliConsoleText.SafeValue(kind)}/{CliConsoleText.SafeValue(name)} in tenant {CliConsoleText.SafeValue(tenantId)}.");
            return 1;
        }

        var selected = SelectResourceGrant(options, kind, name, tenantId);
        if (selected is null)
            return 1;

        var connectionFactory = new AuthorizedPrivateAccessConnectionFactory(
            server,
            api,
            new WebSocketPrivateAccessStreamTransport());
        var forwarder = new LocalForwarder(tenantId, selected.GrantId, connectionFactory);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += CancelForward;
        try
        {
            await forwarder.RunAsync(
                localPort,
                cancellation.Token,
                endpoint =>
                {
                    Console.WriteLine($"Tenant: {CliConsoleText.SafeValue(tenantId)}");
                    Console.WriteLine($"Resource: {CliConsoleText.SafeValue(kind)}/{CliConsoleText.SafeValue(name)}");
                    Console.WriteLine($"Endpoint: {CliConsoleText.SafeValue(selected.EndpointId)}");
                    Console.WriteLine($"Listening: {endpoint.Address}:{endpoint.Port}");
                    Console.WriteLine("Press Ctrl+C to stop.");
                });
        }
        finally
        {
            Console.CancelKeyPress -= CancelForward;
        }

        return 0;

        void CancelForward(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        }
    }

    private int Logout()
    {
        tokenStore.Clear();
        Console.WriteLine("Signed out of the saved Pole server on this device.");
        return 0;
    }

    private async
        Task<(Uri Server, PoleServerIdentity Identity, OidcAccessTokenProvider Provider, string AccountKey,
            IReadOnlyList<TenantDto> Tenants)> GetAuthenticatedTenantsAsync(CancellationToken cancellationToken)
    {
        var profile = await LoadProfileAsync(cancellationToken) ??
            throw new InvalidOperationException("No Pole server is saved. Run 'pole login --server <url>' first.");
        var (server, identity) = profile;
        var provider = CreateTokenProvider(server, identity);
        var accessToken = await provider.GetAccessTokenAsync(cancellationToken, allowInteractiveLogin: false);
        var accountKey = CliAccountIdentity.GetAccountKey(accessToken, identity.Issuer, server);
        var tenants = await CreateApi(server, provider, allowInteractiveLogin: false)
            .GetTenantsAsync(cancellationToken);
        return (server, identity, provider, accountKey, tenants);
    }

    private async Task<(Uri Server, PoleServerIdentity Identity)?> LoadProfileAsync(CancellationToken cancellationToken)
    {
        var saved = await tokenStore.LoadProfileAsync(cancellationToken);
        return saved;
    }

    private OidcAccessTokenProvider CreateTokenProvider(Uri server, PoleServerIdentity identity) => new(
        identity.Issuer,
        identity.ClientId,
        server,
        authenticator,
        tokenStore);

    private PrivateAccessApiClient CreateApi(
        Uri server,
        OidcAccessTokenProvider provider,
        bool allowInteractiveLogin) => new(
        httpClient,
        server,
        cancellationToken => provider.GetAccessTokenAsync(cancellationToken, allowInteractiveLogin));

    private static bool TryReadServerOption(string[] arguments, out Uri? server)
    {
        server = null;
        if (arguments.Length == 0)
            return true;
        if (arguments.Length != 2 || arguments[0] != "--server" ||
            !Uri.TryCreate(arguments[1], UriKind.Absolute, out var parsed))
            return false;
        server = parsed;
        return true;
    }

    private static bool TryReadForwardOptions(
        string[] arguments,
        out string? deploymentId,
        out string? endpointId,
        out int localPort)
    {
        deploymentId = null;
        endpointId = null;
        localPort = 0;
        var localPortSpecified = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            if (index + 1 >= arguments.Length)
                return false;
            var option = arguments[index++];
            var value = arguments[index];
            switch (option)
            {
                case "--deployment" when deploymentId is null && !string.IsNullOrWhiteSpace(value):
                    deploymentId = value;
                    break;
                case "--endpoint" when endpointId is null && !string.IsNullOrWhiteSpace(value):
                    endpointId = value;
                    break;
                case "--local-port" when !localPortSpecified && int.TryParse(value, out var parsedPort) &&
                    parsedPort is >= 0 and <= 65535:
                    localPort = parsedPort;
                    localPortSpecified = true;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static TenantDto? PromptForTenant(IReadOnlyList<TenantDto> tenants)
    {
        if (Console.IsInputRedirected)
            return null;
        for (var index = 0; index < tenants.Count; index++)
            Console.WriteLine(
                $"{index + 1}. {CliConsoleText.SafeValue(tenants[index].Name)} ({CliConsoleText.SafeValue(tenants[index].Id)})");
        Console.Write("Tenant number: ");
        return int.TryParse(Console.ReadLine(), out var choice) && choice >= 1 && choice <= tenants.Count
            ? tenants[choice - 1]
            : null;
    }

    private static PrivateAccessResourceGrantDto? SelectResourceGrant(
        IReadOnlyList<PrivateAccessResourceGrantDto> options,
        string kind,
        string name,
        string tenantId)
    {
        if (options.Count == 1)
            return options[0];
        Console.Error.WriteLine(
            $"Several eligible routes exist for {CliConsoleText.SafeValue(kind)}/{CliConsoleText.SafeValue(name)} in tenant {CliConsoleText.SafeValue(tenantId)}:");
        for (var index = 0; index < options.Count; index++)
            Console.Error.WriteLine(
                $"{index + 1}. deployment {CliConsoleText.SafeValue(options[index].ResourceDeploymentId)}, endpoint {CliConsoleText.SafeValue(options[index].EndpointId)}, grant {CliConsoleText.SafeValue(options[index].GrantId)}");
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine(
                "Use --deployment and --endpoint to narrow the route, then retry interactively if several grants remain.");
            return null;
        }

        Console.Write("Route number: ");
        return int.TryParse(Console.ReadLine(), out var choice) && choice >= 1 && choice <= options.Count
            ? options[choice - 1]
            : null;
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: pole login [--server <url>] | pole status | pole tenant list | pole tenant select <tenant-id> | pole forward <kind>/<name> [--deployment <id>] [--endpoint <id>] [--local-port <port>] | pole logout");
        return 2;
    }
}
