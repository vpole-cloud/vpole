using System.Net.Http.Json;
using System.Text.Json;

namespace Pole.Cli.Authentication;

public sealed record PoleServerIdentity(
    Uri Issuer,
    string ClientId);

public sealed class PoleServerDiscovery(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PoleServerIdentity> DiscoverAsync(Uri server, CancellationToken cancellationToken)
    {
        CliTokenStore.ValidateServer(server);
        var discoveryUri = new Uri(server, "/.well-known/pole-cli");
        using var response = await httpClient.GetAsync(discoveryUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Pole did not provide its public CLI authentication settings.");

        var settings = await response.Content.ReadFromJsonAsync<DiscoveryDocument>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Pole returned an empty CLI authentication document.");
        if (!Uri.TryCreate(settings.Issuer, UriKind.Absolute, out var issuer) ||
            string.IsNullOrWhiteSpace(settings.ClientId) || settings.ClientId.Length > 256 ||
            settings.ClientId.Any(char.IsControl))
            throw new InvalidOperationException("Pole returned invalid CLI authentication settings.");

        OidcAuthenticator.ValidateDiscoveredIssuer(issuer);
        return new PoleServerIdentity(issuer, settings.ClientId);
    }

    private sealed record DiscoveryDocument(
        string Issuer,
        string ClientId);
}
