// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Pole.Features.Users.Contracts;

namespace Pole.Features.Users.Application.Identity;

public sealed class KeycloakIdentityProvider(
    HttpClient httpClient,
    IOptions<KeycloakOptions> options
) : IIdentityProvider
{
    private readonly KeycloakOptions _options = options.Value;

    public Task<CreateIdentityUserOutcome>
        CreateUserAsync(CreateIdentityUser user, CancellationToken cancellationToken) =>
        CreateUserCoreAsync(user, false, cancellationToken);

    private async Task<CreateIdentityUserOutcome> CreateUserCoreAsync(
        CreateIdentityUser user,
        bool invitationSetup,
        CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        if (token is null)
            return new IdentityUserCreationFailed();

        using var request = new HttpRequestMessage(HttpMethod.Post, AdminUsersPath());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(
            new KeycloakCreateUserRequest(
                Username: user.Email,
                Email: user.Email,
                FirstName: user.FirstName,
                LastName: user.LastName,
                Enabled: true,
                RequiredActions: ["UPDATE_PASSWORD", "VERIFY_EMAIL"],
                Attributes: invitationSetup
                    ? new Dictionary<string, string[]> { ["pole-invitation-setup"] = ["true"] }
                    : null));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new IdentityUserCreationFailed();

        var idFromLocation = TryReadUserIdFromLocation(response.Headers.Location);
        if (!string.IsNullOrWhiteSpace(idFromLocation))
            return new IdentityUserCreated(idFromLocation);

        var idFromLookup = await LookupUserIdByUsernameAsync(user.Email, token, cancellationToken);
        return idFromLookup is null ? new IdentityUserCreationFailed() : new IdentityUserCreated(idFromLookup);
    }

    public async Task<StartInvitedIdentityOutcome> StartInvitationAsync(
        StartInvitedIdentityRequest invitation,
        CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        if (token is null)
            return new InvitedIdentityUnavailable();
        var identity = await LookupByEmailAsync(invitation.Email, token, cancellationToken);
        var created = false;
        if (identity is null)
        {
            var outcome = await CreateUserCoreAsync(
                new CreateIdentityUser(invitation.Email, null, null),
                true,
                cancellationToken);
            if (outcome is IdentityUserCreated user)
            {
                identity = new KeycloakInvitationIdentity(
                    user.UserId,
                    invitation.Email,
                    false,
                    new Dictionary<string, string[]> { ["pole-invitation-setup"] = ["true"] },
                    ["VERIFY_EMAIL", "UPDATE_PASSWORD"]);
                created = true;
            }
            else
            {
                // A simultaneous invitation may have provisioned this email. Existing credentials remain untouched.
                identity = await LookupByEmailAsync(invitation.Email, token, cancellationToken);
                if (identity is null)
                    return new InvitedIdentityUnavailable();
            }
        }

        if (!string.Equals(identity.Email, invitation.Email, StringComparison.OrdinalIgnoreCase))
            return new InvitedIdentityUnavailable();
        var unfinishedInvitationSetup = created ||
            (identity.Attributes?.TryGetValue("pole-invitation-setup", out var markers) == true &&
                markers.Contains("true") && identity.RequiredActions?.Contains("UPDATE_PASSWORD") == true);
        if (unfinishedInvitationSetup && !created)
            unfinishedInvitationSetup = !await HasPasswordAsync(identity.Id, token, cancellationToken);
        if (!unfinishedInvitationSetup && identity.EmailVerified)
            return new InvitedIdentityStarted(identity.Id, false);

        var actions = unfinishedInvitationSetup
            ? new[] { "VERIFY_EMAIL", "UPDATE_PASSWORD" }
            : new[] { "VERIFY_EMAIL" };
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{AdminUsersPath()}/{Uri.EscapeDataString(identity.Id)}/execute-actions-email" +
            $"?client_id={Uri.EscapeDataString(_options.FrontendClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(invitation.RedirectUri)}&lifespan=604800");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(actions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? new InvitedIdentityStarted(identity.Id, true)
            : new InvitedIdentityUnavailable();
    }

    private async Task<bool> HasPasswordAsync(
        string subject,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{AdminUsersPath()}/{Uri.EscapeDataString(subject)}/credentials");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var credentials = await response.Content.ReadFromJsonAsync<KeycloakCredential[]>(cancellationToken);
        return credentials?.Any(credential => credential.Type == "password") == true;
    }

    private async Task<KeycloakInvitationIdentity?> LookupByEmailAsync(
        string email,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{AdminUsersPath()}?email={Uri.EscapeDataString(email)}&exact=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var identities = await response.Content.ReadFromJsonAsync<KeycloakInvitationIdentity[]>(cancellationToken);
        return identities?.SingleOrDefault();
    }

    public async Task<bool> DeleteUserAsync(string userId, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        if (token is null)
            return false;

        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{AdminUsersPath()}/{Uri.EscapeDataString(userId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
    }

    private async Task<string?> LookupUserIdByUsernameAsync(
        string username,
        string token,
        CancellationToken cancellationToken)
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
        using var content = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.AdminClientId,
                ["client_secret"] = _options.AdminClientSecret,
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

    private sealed record KeycloakCredential(
        [property: JsonPropertyName("type")] string Type
    );

    private sealed record KeycloakInvitationIdentity(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("emailVerified")]
        bool EmailVerified,
        [property: JsonPropertyName("attributes")]
        IReadOnlyDictionary<string, string[]>? Attributes,
        [property: JsonPropertyName("requiredActions")]
        IReadOnlyList<string>? RequiredActions
    );

    private sealed record KeycloakTokenResponse(
        [property: JsonPropertyName("access_token")]
        string AccessToken
    );

    private sealed record KeycloakUserLookupResponse(
        [property: JsonPropertyName("id")] string Id
    );

    private sealed record KeycloakCreateUserRequest(
        string Username,
        string Email,
        string? FirstName,
        string? LastName,
        bool Enabled,
        IReadOnlyCollection<string> RequiredActions,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyDictionary<string, string[]>? Attributes
    );
}
