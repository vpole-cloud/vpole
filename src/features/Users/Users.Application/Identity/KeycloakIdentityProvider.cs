using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Pole.Features.Users.Application.Identity;

public sealed class KeycloakIdentityProvider(
    HttpClient httpClient,
    IOptions<KeycloakOptions> options)
    : IIdentityProvider
{
    private readonly KeycloakOptions _options = options.Value;

    public async Task<CreateIdentityUserOutcome> CreateUserAsync(
        CreateIdentityUser user,
        CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        if (token is null)
            return new IdentityUserCreationFailed();

        using var request = new HttpRequestMessage(HttpMethod.Post, AdminUsersPath());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new KeycloakCreateUserRequest(
            Username: user.Email,
            Email: user.Email,
            FirstName: user.FirstName,
            LastName: user.LastName,
            Enabled: true,
            RequiredActions: ["UPDATE_PASSWORD", "VERIFY_EMAIL"]));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new IdentityUserCreationFailed();
        }

        var idFromLocation = TryReadUserIdFromLocation(response.Headers.Location);
        if (!string.IsNullOrWhiteSpace(idFromLocation))
            return new IdentityUserCreated(idFromLocation);

        var idFromLookup = await LookupUserIdByUsernameAsync(user.Email, token, cancellationToken);
        return idFromLookup is null
            ? new IdentityUserCreationFailed()
            : new IdentityUserCreated(idFromLookup);
    }

    public async Task<bool> DeleteUserAsync(string userId, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        if (token is null)
            return false;

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{AdminUsersPath()}/{Uri.EscapeDataString(userId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
    }

    private async Task<string?> LookupUserIdByUsernameAsync(string username, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{AdminUsersPath()}?username={Uri.EscapeDataString(username)}&exact=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        var users = await response.Content.ReadFromJsonAsync<KeycloakUserLookupResponse[]>(cancellationToken);
        return users?.FirstOrDefault()?.Id;
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _options.AdminClientId,
            ["client_secret"] = _options.AdminClientSecret
        });
        using var response = await httpClient.PostAsync(
            $"{_options.BaseUrl.TrimEnd('/')}/realms/{Uri.EscapeDataString(_options.Realm)}/protocol/openid-connect/token",
            content,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        var token = await response.Content.ReadFromJsonAsync<KeycloakTokenResponse>(cancellationToken);
        return token?.AccessToken;
    }

    private string AdminUsersPath() =>
        $"{_options.BaseUrl.TrimEnd('/')}/admin/realms/{Uri.EscapeDataString(_options.Realm)}/users";

    private static string? TryReadUserIdFromLocation(Uri? location)
    {
        if (location is null)
            return null;

        var segments = location.Segments;
        return segments.Length == 0 ? null : Uri.UnescapeDataString(segments[^1].Trim('/'));
    }

    private sealed record KeycloakTokenResponse([property: JsonPropertyName("access_token")] string AccessToken);
    private sealed record KeycloakUserLookupResponse([property: JsonPropertyName("id")] string Id);
    private sealed record KeycloakCreateUserRequest(
        string Username,
        string Email,
        string? FirstName,
        string? LastName,
        bool Enabled,
        IReadOnlyCollection<string> RequiredActions);
}
