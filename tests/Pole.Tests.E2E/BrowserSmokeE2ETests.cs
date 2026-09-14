using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class BrowserSmokeE2ETests(PoleE2EFixture fixture)
{
    [Fact]
    public async Task User_can_sign_in_select_a_tenant_and_create_resource_groups_at_desktop_and_mobile_widths()
    {
        await using var resources = new TestResourceScope("browser smoke E2E");
        await using var browserContainer = new PlaywrightBrowserContainer(fixture.Identity);
        using (var startup = new CancellationTokenSource(TimeSpan.FromMinutes(20)))
            await browserContainer.StartAsync(startup.Token);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.ConnectAsync(
            browserContainer.WebSocketEndpoint,
            new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });

        foreach (var (name, width, height) in new[]
        {
            ("desktop", 1440, 900),
            ("mobile", 390, 844)
        })
        {
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = width, Height = height }
            });
            var page = await context.NewPageAsync();
            await page.GotoAsync(fixture.FrontendBaseAddress.ToString(), new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle
            });

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in" }).ClickAsync();
            await page.GetByLabel("Username or email").FillAsync(fixture.Alpha.Email);
            await page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Password" })
                .FillAsync(fixture.Alpha.Password);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In" }).ClickAsync();

            var tenantSelector = page.GetByLabel("Tenant");
            await tenantSelector.WaitForAsync();
            await tenantSelector.SelectOptionAsync(fixture.Alpha.DefaultTenantId);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Resource groups" }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Environment boundaries" }).WaitForAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New group" }).ClickAsync();

            var resourceGroupName = $"{name}-{Guid.NewGuid():N}"[..20];
            await page.GetByLabel("Name").FillAsync(resourceGroupName);
            await page.GetByLabel("Description").FillAsync($"{name} browser smoke test");
            await page.GetByLabel("Tags").FillAsync("e2e, browser");
            var createResponse = await page.RunAndWaitForResponseAsync(
                () => page.GetByRole(
                        AriaRole.Button,
                        new PageGetByRoleOptions { Name = "Create group" })
                    .ClickAsync(),
                response =>
                    response.Request.Method == HttpMethod.Post.Method &&
                    new Uri(response.Url).AbsolutePath.TrimEnd('/') == "/api/v1/resource-groups");
            Assert.True(
                createResponse.Ok,
                $"Resource-group creation returned HTTP {createResponse.Status}.");
            var createdResourceGroup = await createResponse.JsonAsync()
                                       ?? throw new InvalidOperationException(
                                           "The browser-created resource group response was empty.");
            var resourceGroupId = createdResourceGroup.GetProperty("id").GetString()
                                  ?? throw new InvalidOperationException(
                                      "The browser-created resource group has no id.");
            resources.Defer(
                $"delete browser-created resource group '{resourceGroupId}'",
                token => DeleteResourceGroupAsync(resourceGroupId, token));
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = resourceGroupName }).WaitForAsync();
        }
    }

    private async Task DeleteResourceGroupAsync(string resourceGroupId, CancellationToken cancellationToken)
    {
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Delete,
            $"/api/v1/resource-groups/{resourceGroupId}",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }

}
