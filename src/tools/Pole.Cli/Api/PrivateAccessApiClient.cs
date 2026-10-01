using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pole.Cli.Authentication;

namespace Pole.Cli.Api;

public sealed record PrivateAccessGrantDto(
    string Id,
    string TenantId,
    string SubjectUserId,
    string ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string EndpointId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool Revoked);

public sealed record PrivateAccessSessionDto(
    string SessionId,
    string Ticket,
    DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() => $"PrivateAccessSessionDto {{ SessionId = {SessionId}, Ticket = [REDACTED], ExpiresAtUtc = {ExpiresAtUtc:O} }}";
}

public sealed record TenantDto(
    string Id,
    string Name,
    string Role,
    bool IsDefault);

public sealed record PrivateAccessResourceGrantDto(
    string GrantId,
    string ResourceDeploymentId,
    string EndpointId,
    DateTimeOffset ExpiresAtUtc);

public interface IPrivateAccessSessionSource
{
    Task<PrivateAccessSessionDto> CreateSessionAsync(
        string tenantId,
        string grantId,
        CancellationToken cancellationToken);
}

public sealed class PrivateAccessApiClient(
    HttpClient httpClient,
    Uri server,
    Func<CancellationToken, Task<string>> getAccessToken) : IPrivateAccessSessionSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PrivateAccessGrantDto>> GetGrantsAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "api/v1/private-access/grants", tenantId, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        EnsureAuthenticated(response);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Pole did not return Private Access grants.");

        return await response.Content.ReadFromJsonAsync<PrivateAccessGrantDto[]>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Pole returned an invalid Private Access grant list.");
    }

    public async Task<IReadOnlyList<TenantDto>> GetTenantsAsync(CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "api/v1/tenants", tenantId: null, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        EnsureAuthenticated(response);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Pole did not return the signed-in user's tenants.");

        return await response.Content.ReadFromJsonAsync<TenantDto[]>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Pole returned an invalid tenant list.");
    }

    public async Task<IReadOnlyList<PrivateAccessResourceGrantDto>> GetResourceGrantsAsync(
        string tenantId,
        string kind,
        string name,
        string? deploymentId,
        string? endpointId,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(kind, nameof(kind));
        ValidateIdentifier(name, nameof(name));
        var path = $"api/v1/private-access/resources/{Uri.EscapeDataString(kind)}/{Uri.EscapeDataString(name)}/grants";
        var filters = new List<string>();
        if (deploymentId is not null)
        {
            ValidateIdentifier(deploymentId, nameof(deploymentId));
            filters.Add("deploymentId=" + Uri.EscapeDataString(deploymentId));
        }
        if (endpointId is not null)
        {
            ValidateIdentifier(endpointId, nameof(endpointId));
            filters.Add("endpointId=" + Uri.EscapeDataString(endpointId));
        }
        if (filters.Count > 0)
            path += "?" + string.Join('&', filters);

        using var request = await CreateRequestAsync(HttpMethod.Get, path, tenantId, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        EnsureAuthenticated(response);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Pole could not resolve eligible Private Access grants for this resource.");

        var result = await response.Content.ReadFromJsonAsync<ResourceGrantList>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Pole returned an invalid resource grant list.");
        return result.Items;
    }

    public async Task<PrivateAccessSessionDto> CreateSessionAsync(
        string tenantId,
        string grantId,
        CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(
            HttpMethod.Post,
            $"api/v1/private-access/grants/{Uri.EscapeDataString(grantId)}/sessions",
            tenantId,
            cancellationToken);
        request.Content = new ByteArrayContent([]);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        EnsureAuthenticated(response);
        if (response.StatusCode != System.Net.HttpStatusCode.Created)
            throw new InvalidOperationException("Pole could not authorize a new Private Access session.");

        var session = await response.Content.ReadFromJsonAsync<PrivateAccessSessionDto>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Pole returned an invalid Private Access session.");
        if (string.IsNullOrWhiteSpace(session.SessionId) ||
            session.SessionId.Length > 256 ||
            !IsHttpToken(session.Ticket) ||
            session.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            session.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidOperationException("Pole returned an invalid Private Access session ticket.");

        return session;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string relativePath,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        CliTokenStore.ValidateServer(server);
        var token = await getAccessToken(cancellationToken);
        var request = new HttpRequestMessage(method, new Uri(server, "/" + relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantId is not null)
        {
            ValidateIdentifier(tenantId, nameof(tenantId));
            request.Headers.Add("X-Pole-Tenant-ID", tenantId);
        }
        return request;
    }

    internal static bool IsHttpToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 4096 &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' ||
            "!#$%&'*+-.^_`|~".Contains(character));

    private static void EnsureAuthenticated(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new CliLoginRequiredException();
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("A valid Private Access identifier is required.", parameterName);
    }

    private sealed record ResourceGrantList(IReadOnlyList<PrivateAccessResourceGrantDto> Items);
}
