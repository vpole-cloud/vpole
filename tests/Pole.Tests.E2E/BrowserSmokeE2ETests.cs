using Microsoft.Playwright;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class BrowserSmokeE2ETests(PoleE2EFixture fixture)
{
    [Fact]
    public async Task User_can_sign_in_select_a_tenant_and_create_resource_groups_at_desktop_and_mobile_widths()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

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
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create group" }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = resourceGroupName }).WaitForAsync();
        }
    }
}
