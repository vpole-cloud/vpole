// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Pole.Features.Core.Multitenancy;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

public sealed partial class PoleE2EFixture
{
    public async Task<JsonElement> InviteAndJoinAsync(
        string tenantId,
        TestUser issuer,
        TestUser recipient,
        TenantRole role,
        CancellationToken ct)
    {
        var previous = await ReadMailIdsAsync(recipient.Email, ct);
        using var create = CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/invitations",
            issuer);
        create.Content = JsonContent.Create(new { recipient.Email, role = role.ToString() });
        using var created = await Api.SendAsync(create, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var link = await WaitForMailLinkAsync(recipient.Email, previous, false, ct);
        using var accept = CreateAuthenticatedRequest(HttpMethod.Post, "/api/v1/tenant-invitations/accept", recipient);
        accept.Content
            = JsonContent.Create(new { token = QueryHelpers.ParseQuery(link.Fragment[1..])["token"].Single() });
        using var accepted = await Api.SendAsync(accept, ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var members = await GetMembersAsync(tenantId, issuer, ct);
        return Assert.Single(
            members.EnumerateArray(),
            member => member.GetProperty("userId").GetString() == recipient.KeycloakUserId);
    }

    public async Task<JsonElement> GetMembersAsync(
        string tenantId,
        TestUser issuer,
        CancellationToken ct)
    {
        using var read = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/tenants/{tenantId}/members", issuer);
        using var response = await Api.SendAsync(read, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public async Task<HashSet<string>> ReadMailIdsAsync(string email, CancellationToken ct)
    {
        using var mail = _application!.CreateHttpClient("pole-mail", "http");
        var list = await mail.GetFromJsonAsync<JsonElement>(
            "/api/v1/search?query=" + Uri.EscapeDataString("to:" + email),
            ct);
        return list.GetProperty("messages")
            .EnumerateArray()
            .Where(message => message.GetProperty("To")
                .EnumerateArray()
                .Any(to => string.Equals(
                    to.GetProperty("Address").GetString(),
                    email,
                    StringComparison.OrdinalIgnoreCase)))
            .Select(message => message.GetProperty("ID").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<Uri> WaitForMailLinkAsync(
        string email,
        HashSet<string> previous,
        bool keycloak,
        CancellationToken ct)
    {
        using var mail = _application!.CreateHttpClient("pole-mail", "http");
        using var identity = _application!.CreateHttpClient("pole-keycloak", "http");
        var origin = keycloak ? identity.BaseAddress! : FrontendBaseAddress;
        Uri? link = null;
        await TestWait.UntilAsync(
            async token =>
            {
                var ids = await ReadMailIdsAsync(email, token);
                foreach (var id in ids.Except(previous))
                {
                    var message = await mail.GetFromJsonAsync<JsonElement>($"/api/v1/message/{id}", token);
                    var body = message.GetProperty("Text").GetString() + "\n" + message.GetProperty("HTML").GetString();
                    foreach (Match match in MailLinkPattern().Matches(body))
                    {
                        if (Uri.TryCreate(WebUtility.HtmlDecode(match.Value), UriKind.Absolute, out var url) &&
                            url.Scheme == origin.Scheme && url.Port == origin.Port &&
                            (url.Host == origin.Host || (url.IsLoopback && origin.IsLoopback)) && (keycloak
                                ? url.AbsolutePath.EndsWith("/login-actions/action-token", StringComparison.Ordinal)
                                : url.AbsolutePath == "/invitations" && url.Fragment.StartsWith(
                                    "#token=",
                                    StringComparison.Ordinal)))
                        {
                            link = url;
                            return true;
                        }
                    }
                }

                return false;
            },
            TimeSpan.FromMinutes(2),
            "The expected invitation email link did not arrive.",
            ct,
            TimeSpan.FromSeconds(1));
        return link!;
    }

    [GeneratedRegex("""https?://[^\s<>"']+""")]
    private static partial Regex MailLinkPattern();
}
