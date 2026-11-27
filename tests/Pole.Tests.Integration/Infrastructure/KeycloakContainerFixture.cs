// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Options;
using Pole.Features.Users.Application.Identity;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class KeycloakContainerFixture : IAsyncLifetime
{
    private const int KeycloakPort = 8080;
    private readonly IContainer _container;
    private readonly HttpClient _httpClient = new();

    public KeycloakContainerFixture()
    {
        var realmFile = ResolveRealmFile();
        var identity = TestRunIdentity.Create();

        _container = new ContainerBuilder("quay.io/keycloak/keycloak:26.4.7").WithTestOwnership(identity)
            .WithEnvironment("POLE_SMTP_HOST", "localhost")
            .WithEnvironment("POLE_SMTP_PORT", "1025")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin")
            .WithResourceMapping(new FileInfo(realmFile), "/opt/keycloak/data/import/")
            .WithPortBinding(KeycloakPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(KeycloakPort))
            .WithCommand("start-dev", "--import-realm")
            .Build();
    }

    public string BaseUrl { get; private set; } = "";
    public string Realm { get; } = "pole.control-plane";
    public string AdminClientId { get; } = "pole.control-plane.admin";
    public string AdminClientSecret { get; } = "dev-admin-secret";
    public KeycloakIdentityProvider IdentityProvider { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        BaseUrl = $"http://127.0.0.1:{_container.GetMappedPublicPort(KeycloakPort)}";
        IdentityProvider = CreateIdentityProvider(AdminClientSecret);

        await WaitForRealmAsync();
    }

    public async Task DisposeAsync()
    {
        _httpClient.Dispose();
        await _container.DisposeAsync();
    }

    public KeycloakIdentityProvider CreateIdentityProvider(string adminClientSecret)
    {
        var options = Options.Create(
            new KeycloakOptions
            {
                BaseUrl = BaseUrl,
                Realm = Realm,
                AdminClientId = AdminClientId,
                AdminClientSecret = adminClientSecret,
            });

        return new KeycloakIdentityProvider(new HttpClient(), options);
    }

    public async Task<string?> GetAdminAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var result = await ProbeAdminAccessTokenAsync(cancellationToken);
        return result.Token;
    }

    private async Task<(string? Token, HttpStatusCode StatusCode)> ProbeAdminAccessTokenAsync(
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = AdminClientId,
                ["client_secret"] = AdminClientSecret,
            });

        using var response = await _httpClient.PostAsync(
            $"{BaseUrl}/realms/{Uri.EscapeDataString(Realm)}/protocol/openid-connect/token",
            content,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            return (null, response.StatusCode);

        var token = await response.Content.ReadFromJsonAsync<KeycloakTokenResponse>(cancellationToken);
        return (token?.AccessToken, response.StatusCode);
    }

    public async Task<KeycloakAdminUser?> FindUserByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        var users = await FindUsersByUsernameAsync(username, cancellationToken);
        return users.FirstOrDefault();
    }

    public async Task DeleteUserIfExistsAsync(string username, CancellationToken cancellationToken = default)
    {
        var users = await FindUsersByUsernameAsync(username, cancellationToken);
        foreach (var user in users)
            await DeleteUserByIdAsync(user.Id, cancellationToken);
    }

    public async Task DeleteUserByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        var token = await GetAdminAccessTokenAsync(cancellationToken) ??
            throw new InvalidOperationException("Unable to get Keycloak admin token.");

        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{BaseUrl}/admin/realms/{Uri.EscapeDataString(Realm)}/users/{Uri.EscapeDataString(userId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<KeycloakAdminUser[]> FindUsersByUsernameAsync(
        string username,
        CancellationToken cancellationToken)
    {
        var token = await GetAdminAccessTokenAsync(cancellationToken) ??
            throw new InvalidOperationException("Unable to get Keycloak admin token.");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{BaseUrl}/admin/realms/{Uri.EscapeDataString(Realm)}/users?username={Uri.EscapeDataString(username)}&exact=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<KeycloakAdminUser[]>(cancellationToken) ?? [];
    }

    private async Task WaitForRealmAsync()
    {
        var timeoutAt = DateTime.UtcNow.AddMinutes(2);
        Exception? lastError = null;
        HttpStatusCode? lastStatusCode = null;

        while (DateTime.UtcNow < timeoutAt)
        {
            try
            {
                var result = await ProbeAdminAccessTokenAsync(CancellationToken.None);
                lastStatusCode = result.StatusCode;
                if (!string.IsNullOrWhiteSpace(result.Token))
                    return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            "Keycloak realm did not become ready in time. Last token endpoint status: " +
            (lastStatusCode is { } status ? ((int)status).ToString() : "no response"),
            lastError);
    }

    private static string ResolveRealmFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "infrastructure",
                "keycloak",
                "realms",
                "pole.control-plane.realm.json");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Unable to find infrastructure/keycloak/realms/pole.control-plane.realm.json.");
    }

    private sealed record KeycloakTokenResponse(
        [property: JsonPropertyName("access_token")]
        string AccessToken
    );
}

public sealed record KeycloakAdminUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("username")]
    string Username,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("firstName")]
    string? FirstName,
    [property: JsonPropertyName("lastName")]
    string? LastName,
    [property: JsonPropertyName("enabled")]
    bool Enabled,
    [property: JsonPropertyName("requiredActions")]
    IReadOnlyCollection<string>? RequiredActions,
    [property: JsonPropertyName("attributes")]
    IReadOnlyDictionary<string, string[]>? Attributes
);
