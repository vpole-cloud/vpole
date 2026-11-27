// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class TenantInvitationBrowserTests(
    PoleE2EFixture fixture
)
{
    [Fact]
    public async Task Existing_recipient_signs_in_and_accepts_without_changing_their_default()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = budget.Token;
        using var evidence
            = new TestPhaseJournal(nameof(Existing_recipient_signs_in_and_accepts_without_changing_their_default))
                .Activate();
        var tenant = fixture.Alpha.DefaultTenantId;
        var email = fixture.Beta.Email;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.ConnectAsync(
            await fixture.GetBrowserEndpointAsync(ct),
            new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
        await using var context = await browser.NewContextAsync();
        var page = await BrowserEvidence.NewPageAsync(context);
        var previous = await fixture.ReadMailIdsAsync(email, ct);
        await using var ownerContext = await browser.NewContextAsync();
        var ownerPage = await BrowserEvidence.NewPageAsync(ownerContext);
        await ownerPage.GotoAsync(fixture.FrontendBaseAddress.AbsoluteUri);
        await Button(ownerPage, "Log in").ClickAsync();
        await SignInAsync(ownerPage, fixture.Alpha.Email, fixture.Alpha.Password);
        await Assertions.Expect(ownerPage.GetByRole(AriaRole.Combobox, new() { Name = "Tenant", Exact = true }))
            .ToHaveValueAsync(tenant);
        await ownerPage.GetByRole(AriaRole.Button, new() { Name = "Members", Exact = true }).ClickAsync();
        await Assertions.Expect(ownerPage.GetByRole(AriaRole.Heading, new() { Name = "Invite member" }))
            .ToBeVisibleAsync();
        await ownerPage.GetByLabel("Email address").FillAsync(email);
        await ownerPage.GetByLabel("Role", new() { Exact = true }).SelectOptionAsync("Viewer");
        var issued = await ownerPage.RunAndWaitForResponseAsync(
            () => Button(ownerPage, "Send invitation").ClickAsync(),
            response => response.Request.Method == "POST" &&
                new Uri(response.Url).AbsolutePath == $"/api/v1/tenants/{tenant}/invitations");
        Assert.Equal(201, issued.Status);
        var invite = (await issued.JsonAsync())!.Value.GetProperty("id").GetString()!;
        var resources = new TestResourceScope("existing invitation");
        await resources.RunAsync(async () =>
        {
            resources.Defer("remove owned invitation or membership", cleanup => CleanupAsync(invite, email, cleanup));
            var link = await fixture.WaitForMailLinkAsync(email, previous, false, ct);
            var secret = QueryHelpers.ParseQuery(link.Fragment[1..])["token"].Single()!;
            var disclosed = 0;
            page.Request += (_, request) =>
            {
                var target = new Uri(request.Url);
                if (target.AbsolutePath.StartsWith("/realms/", StringComparison.Ordinal) &&
                    (target.Query.Contains(secret, StringComparison.Ordinal) ||
                        request.PostData?.Contains(secret, StringComparison.Ordinal) == true))
                    Interlocked.Increment(ref disclosed);
            };
            await OpenLinkAsync(page, link);
            await Assertions.Expect(page.GetByText(email, new() { Exact = false }).First).ToBeVisibleAsync();
            await AssertNoMemberAsync(email, ct);
            await Button(page, "Sign in").ClickAsync();
            await SignInAsync(page, email, fixture.Beta.Password);
            await Button(page, "Accept invitation").ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Tenant", Exact = true }))
                .ToHaveValueAsync(tenant);
            Assert.Equal(0, Volatile.Read(ref disclosed));
            using var read = fixture.CreateAuthenticatedRequest(HttpMethod.Get, "/api/v1/tenants", fixture.Beta);
            using var response = await fixture.Api.SendAsync(read, ct);
            response.EnsureSuccessStatusCode();
            var tenants = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(
                fixture.Beta.DefaultTenantId,
                Assert.Single(tenants.EnumerateArray(), item => item.GetProperty("isDefault").GetBoolean())
                    .GetProperty("id")
                    .GetString());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_registration_uses_Keycloak_actions_and_requires_a_live_invitation(
        bool revokeDuringRegistration)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = budget.Token;
        using var evidence
            = new TestPhaseJournal(nameof(First_registration_uses_Keycloak_actions_and_requires_a_live_invitation))
                .Activate();
        var email = $"join-{Guid.NewGuid():N}@e2e.pole.test";
        var password = $"Pole-{Guid.NewGuid():N}-1!";
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.ConnectAsync(
            await fixture.GetBrowserEndpointAsync(ct),
            new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
        await using var context = await browser.NewContextAsync();
        var page = await BrowserEvidence.NewPageAsync(context);
        var invite = await CreateInvitationAsync(email, ct);
        var resources = new TestResourceScope("new recipient invitation");
        await resources.RunAsync(async () =>
        {
            resources.Defer("remove owned invitation or membership", cleanup => CleanupAsync(invite, email, cleanup));
            var link = await fixture.WaitForMailLinkAsync(email, [], false, ct);
            await OpenLinkAsync(page, link);
            await AssertNoMemberAsync(email, ct);
            var previous = await fixture.ReadMailIdsAsync(email, ct);
            await Button(page, "Set up account or verify email").ClickAsync();
            var action = await fixture.WaitForMailLinkAsync(email, previous, true, ct);
            if (revokeDuringRegistration)
            {
                await RevokeAsync(invite, ct);
            }
            else
            {
                // Simulate losing the first Keycloak email. The replacement must still set the initial password.
                previous = await fixture.ReadMailIdsAsync(email, ct);
                await Button(page, "Set up account or verify email").ClickAsync();
                action = await fixture.WaitForMailLinkAsync(email, previous, true, ct);
            }

            await OpenLinkAsync(page, action);
            var proceed = page.GetByRole(AriaRole.Link, new() { Name = "Click here to proceed" });
            if (await proceed.IsVisibleAsync())
                await proceed.ClickAsync();
            await page.Locator("#password-new").FillAsync(password);
            await page.Locator("#password-confirm").FillAsync(password);
            await page.Locator("input[type=submit], button[type=submit]").First.ClickAsync();
            // Keycloak can finish actions in another browser. Reopen the original invitation there.
            await using var fresh = await browser.NewContextAsync();
            var join = await BrowserEvidence.NewPageAsync(fresh);
            await OpenLinkAsync(join, link);
            if (revokeDuringRegistration)
            {
                await Assertions.Expect(join.GetByRole(AriaRole.Heading, new() { Name = "Invitation unavailable" }))
                    .ToBeVisibleAsync();
                await AssertNoMemberAsync(email, ct);
                return;
            }

            await Button(join, "Sign in").ClickAsync();
            await SignInAsync(join, email, password);
            // Keycloak's Verify Profile action requires a name for newly enrolled accounts.
            var firstName = join.Locator("#firstName");
            var accept = Button(join, "Accept invitation");
            try
            {
                await firstName.Or(accept).First.WaitForAsync(new() { State = WaitForSelectorState.Visible });
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(
                    $"Unexpected onboarding page after sign-in (path {new Uri(join.Url).AbsolutePath}, title {await join.TitleAsync()}).");
            }

            if (await firstName.IsVisibleAsync())
            {
                await firstName.FillAsync("Pole");
                await join.Locator("#lastName").FillAsync("Invitee");
                await join.Locator("input[type=submit], button[type=submit]").First.ClickAsync();
            }

            await AssertNoMemberAsync(email, ct);
            await Button(join, "Accept invitation").ClickAsync();
            var tenant = join.GetByRole(AriaRole.Combobox, new() { Name = "Tenant", Exact = true });
            await Assertions.Expect(tenant).ToHaveValueAsync(fixture.Alpha.DefaultTenantId);
            await Assertions.Expect(tenant.Locator("option[value]:not([value=''])")).ToHaveCountAsync(1);
            var members = await fixture.GetMembersAsync(fixture.Alpha.DefaultTenantId, fixture.Alpha, ct);
            var member = Assert.Single(
                members.EnumerateArray(),
                entry => entry.GetProperty("email").GetString() == email);
            Assert.True(member.GetProperty("isDefault").GetBoolean());
            await DeleteMemberAsync(member.GetProperty("id").GetString()!, ct);
            await join.ReloadAsync();
            await Assertions.Expect(join.GetByText("No tenant access", new() { Exact = true })).ToBeVisibleAsync();
        });
    }

    private static ILocator Button(IPage page, string name) =>
        page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    private static async Task OpenLinkAsync(IPage page, Uri url)
    {
        try
        {
            await page.GotoAsync(url.AbsoluteUri);
        }
        catch (PlaywrightException)
        {
            throw new InvalidOperationException("Browser navigation to the invitation or identity provider failed.");
        }
    }

    private static async Task SignInAsync(
        IPage page,
        string email,
        string password)
    {
        await page.GetByLabel("Username or email").FillAsync(email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await Button(page, "Sign In").ClickAsync();
    }

    private async Task<string> CreateInvitationAsync(string email, CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/v1/tenants/{fixture.Alpha.DefaultTenantId}/invitations",
            fixture.Alpha);
        request.Content = JsonContent.Create(new { email, role = "Viewer" });
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetString()!;
    }

    private async Task AssertNoMemberAsync(string email, CancellationToken ct)
    {
        var members = await fixture.GetMembersAsync(fixture.Alpha.DefaultTenantId, fixture.Alpha, ct);
        Assert.DoesNotContain(members.EnumerateArray(), entry => entry.GetProperty("email").GetString() == email);
    }

    private async Task CleanupAsync(
        string invite,
        string email,
        CancellationToken ct)
    {
        var members = await fixture.GetMembersAsync(fixture.Alpha.DefaultTenantId, fixture.Alpha, ct);
        foreach (var member in members.EnumerateArray().Where(item => item.GetProperty("email").GetString() == email))
            await DeleteMemberAsync(member.GetProperty("id").GetString()!, ct);
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/v1/tenants/{fixture.Alpha.DefaultTenantId}/invitations",
            fixture.Alpha);
        using var response = await fixture.Api.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var pending = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (pending.EnumerateArray()
            .Any(item => item.GetProperty("id").GetString() == invite &&
                item.GetProperty("status").GetString() == "Pending"))
            await RevokeAsync(invite, ct);
    }

    private async Task RevokeAsync(string invite, CancellationToken ct)
    {
        using var read = fixture.CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/v1/tenants/{fixture.Alpha.DefaultTenantId}/invitations",
            fixture.Alpha);
        using var current = await fixture.Api.SendAsync(read, ct);
        current.EnsureSuccessStatusCode();
        var invitations = await current.Content.ReadFromJsonAsync<JsonElement>(ct);
        var revision = invitations.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == invite)
            .GetProperty("revision")
            .GetInt64();
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/v1/tenants/{fixture.Alpha.DefaultTenantId}/invitations/{invite}/revoke",
            fixture.Alpha);
        request.Content = JsonContent.Create(new { expectedRevision = revision });
        using var response = await fixture.Api.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task DeleteMemberAsync(string memberId, CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Delete,
            $"/api/v1/tenants/{fixture.Alpha.DefaultTenantId}/members/{memberId}",
            fixture.Alpha);
        using var response = await fixture.Api.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
