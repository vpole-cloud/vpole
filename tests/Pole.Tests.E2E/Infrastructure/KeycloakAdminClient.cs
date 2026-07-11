using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pole.Tests.E2E.Infrastructure;

internal sealed class KeycloakAdminClient(HttpClient httpClient, string adminUsername, string adminPassword)
{
    private const string Realm = "pole.control-plane";
    private string? _accessToken;

    public async Task<JsonObject> GetClientAsync(string clientId, CancellationToken cancellationToken)
    {
        var clients = await GetJsonAsync($"/admin/realms/{Realm}/clients?clientId={Uri.EscapeDataString(clientId)}", cancellationToken);
        var client = clients.AsArray().SingleOrDefault() as JsonObject;
        return client?.DeepClone().AsObject()
               ?? throw new InvalidOperationException($"Keycloak client '{clientId}' was not found.");
    }

    public async Task UpdateClientAsync(string internalId, JsonObject client, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Put, $"/admin/realms/{Realm}/clients/{internalId}", client, cancellationToken);

    public async Task<DirectGrantClient> CreateDirectGrantClientAsync(string clientId, CancellationToken cancellationToken)
    {
        var client = new JsonObject
        {
            ["clientId"] = clientId,
            ["enabled"] = true,
            ["protocol"] = "openid-connect",
            ["publicClient"] = false,
            ["clientAuthenticatorType"] = "client-secret",
            ["standardFlowEnabled"] = false,
            ["implicitFlowEnabled"] = false,
            ["directAccessGrantsEnabled"] = true,
            ["serviceAccountsEnabled"] = false,
            ["fullScopeAllowed"] = true,
            ["defaultClientScopes"] = new JsonArray("basic", "profile", "email", "roles"),
            ["protocolMappers"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "pole api audience",
                    ["protocol"] = "openid-connect",
                    ["protocolMapper"] = "oidc-audience-mapper",
                    ["config"] = new JsonObject
                    {
                        ["included.client.audience"] = "pole.control-plane.api",
                        ["access.token.claim"] = "true",
                        ["introspection.token.claim"] = "true"
                    }
                })
        };

        await SendAsync(HttpMethod.Post, $"/admin/realms/{Realm}/clients", client, cancellationToken);
        var created = await GetClientAsync(clientId, cancellationToken);
        var internalId = created["id"]?.GetValue<string>()
                         ?? throw new InvalidOperationException("Keycloak did not return the direct-grant client id.");
        var secret = await GetJsonAsync($"/admin/realms/{Realm}/clients/{internalId}/client-secret", cancellationToken);
        return new DirectGrantClient(
            clientId,
            internalId,
            secret["value"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Keycloak did not return the direct-grant client secret."));
    }

    public async Task DeleteClientAsync(string internalId, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Delete, $"/admin/realms/{Realm}/clients/{internalId}", null, cancellationToken);

    public async Task<string> CreateUserAsync(string email, string firstName, string lastName, string password, CancellationToken cancellationToken)
    {
        var create = new JsonObject
        {
            ["username"] = email,
            ["email"] = email,
            ["firstName"] = firstName,
            ["lastName"] = lastName,
            ["enabled"] = true,
            ["emailVerified"] = true,
            ["requiredActions"] = new JsonArray()
        };
        using var response = await SendAsync(HttpMethod.Post, $"/admin/realms/{Realm}/users", create, cancellationToken);
        var userId = response.Headers.Location?.Segments.LastOrDefault()?.Trim('/');
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("Keycloak did not return the created user id.");

        await SetPasswordAsync(userId, password, cancellationToken);
        return userId;
    }

    public async Task SetPasswordAsync(string userId, string password, CancellationToken cancellationToken) =>
        await SendAsync(
            HttpMethod.Put,
            $"/admin/realms/{Realm}/users/{userId}/reset-password",
            new JsonObject
            {
                ["type"] = "password",
                ["value"] = password,
                ["temporary"] = false
            },
            cancellationToken);

    public async Task MakeUserInteractiveAsync(string userId, CancellationToken cancellationToken)
    {
        var user = (await GetJsonAsync($"/admin/realms/{Realm}/users/{userId}", cancellationToken)).AsObject();
        user["emailVerified"] = true;
        user["requiredActions"] = new JsonArray();
        await SendAsync(HttpMethod.Put, $"/admin/realms/{Realm}/users/{userId}", user, cancellationToken);
    }

    public async Task AssignRealmRoleAsync(string userId, string roleName, CancellationToken cancellationToken)
    {
        var role = await GetJsonAsync($"/admin/realms/{Realm}/roles/{Uri.EscapeDataString(roleName)}", cancellationToken);
        await SendAsync(
            HttpMethod.Post,
            $"/admin/realms/{Realm}/users/{userId}/role-mappings/realm",
            new JsonArray(role),
            cancellationToken);
    }

    public async Task DeleteUserAsync(string userId, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Delete, $"/admin/realms/{Realm}/users/{userId}", null, cancellationToken);

    public async Task<string> GetPasswordGrantTokenAsync(
        string clientId,
        string clientSecret,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(
            $"/realms/{Realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["username"] = username,
                ["password"] = password,
                ["scope"] = "openid profile email"
            }),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var token = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
        return token?["access_token"]?.GetValue<string>()
               ?? throw new InvalidOperationException("Keycloak did not return an access token.");
    }

    private async Task<JsonNode> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException($"Keycloak returned an empty response for '{path}'.");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        JsonNode? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(cancellationToken));
        if (body is not null)
            request.Content = JsonContent.Create(body);

        var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return response;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken))
            return _accessToken;

        using var response = await httpClient.PostAsync(
            "/realms/master/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = adminUsername,
                ["password"] = adminPassword
            }),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var token = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
        return _accessToken = token?["access_token"]?.GetValue<string>()
                              ?? throw new InvalidOperationException("Keycloak did not return a master-admin token.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"Keycloak returned {(int)response.StatusCode}: {body}", null, response.StatusCode);
    }
}

internal sealed record DirectGrantClient(string ClientId, string InternalId, string Secret);
