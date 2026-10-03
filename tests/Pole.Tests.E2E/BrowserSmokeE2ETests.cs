using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class BrowserSmokeE2ETests(
    PoleE2EFixture fixture,
    ITestOutputHelper output)
{
    [Fact]
    public async Task User_can_manage_image_services_in_separate_workspace_tabs_at_desktop_and_mobile_widths()
    {
        await using var resources = new TestResourceScope("browser smoke E2E");
        await using var browserContainer = new PlaywrightBrowserContainer(fixture.Identity);
        using (var startup = new CancellationTokenSource(TimeSpan.FromMinutes(20)))
            await browserContainer.StartAsync(startup.Token);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.ConnectAsync(
            browserContainer.WebSocketEndpoint,
            new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });

        string? previousServiceName = null;
        foreach (var (name, width, height) in new[]
        {
            ("desktop", 1440, 900),
            ("mobile", 390, 844)
        })
        {
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = width, Height = height },
                // Keycloak's local HTTPS endpoint uses an untrusted development certificate.
                IgnoreHTTPSErrors = true
            });
            var page = await context.NewPageAsync();
            var navigation = new ConcurrentQueue<string>();
            void Record(string item)
            {
                navigation.Enqueue(item);
                while (navigation.Count > 20)
                    navigation.TryDequeue(out _);
            }
            page.Response += (_, response) =>
            {
                var resourceType = response.Request.ResourceType;
                if (resourceType == "document" || response.Status >= 300)
                    Record($"{resourceType} {response.Status} {SafePath(response.Url)}");
            };
            page.RequestFailed += (_, request) =>
                Record($"request-failed {request.ResourceType} {SafePath(request.Url)}");
            page.PageError += (_, error) =>
            {
                var errorText = error.ToString();
                var kind = new[] { "TypeError", "ReferenceError", "SyntaxError", "RangeError" }
                    .FirstOrDefault(errorText.Contains) ?? "Other";
                Record($"page-error {kind}");
            };
            await page.GotoAsync(fixture.FrontendBaseAddress.ToString(), new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle
            });

            try
            {
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in" }).ClickAsync();
            }
            catch (TimeoutException)
            {
                await CaptureAuthDiagnosticAsync(page, name, "login-button", navigation);
                throw;
            }
            try
            {
                await page.GetByLabel("Username or email").FillAsync(fixture.Alpha.Email);
            }
            catch (TimeoutException)
            {
                await CaptureAuthDiagnosticAsync(page, name, "username-field", navigation);
                throw;
            }
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

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Web services", Exact = true }).ClickAsync();
            Assert.Equal(1, await page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Tenant", Exact = true }).CountAsync());
            Assert.Equal(0, await page.GetByText("Active tenant", new PageGetByTextOptions { Exact = true }).CountAsync());
            Assert.Equal(0, await page.GetByText("Session", new PageGetByTextOptions { Exact = true }).CountAsync());
            Assert.Equal(0, await page.Locator("#console-content").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Resource groups", Exact = true }).CountAsync());
            Assert.Equal(0, await page.Locator("form:visible").CountAsync());
            await CaptureWorkspaceAsync(page, name, "catalog");
            var scopesResponseTask = page.WaitForResponseAsync(response => response.Request.Method == HttpMethod.Get.Method &&
                new Uri(response.Url).AbsolutePath.EndsWith("/isolation-scopes", StringComparison.Ordinal));
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New service", Exact = true }).ClickAsync();
            var serviceName = $"browser-{Guid.NewGuid():N}"[..20];
            await page.GetByLabel("Name", new PageGetByLabelOptions { Exact = true }).FillAsync(serviceName);
            await page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Resource group" }).SelectOptionAsync(resourceGroupId);
            await page.GetByLabel("Image registry").FillAsync("docker.io");
            await page.GetByLabel("Image name").FillAsync("library/nginx");
            await page.GetByLabel("Tag", new PageGetByLabelOptions { Exact = true }).FillAsync("1.27.5-alpine");
            await page.GetByLabel("Probe path").FillAsync("/");
            await page.GetByLabel("Probe port").FillAsync("80");
            await page.GetByLabel("Target port").FillAsync("80");
            Assert.True((await scopesResponseTask).Ok, "The browser must load named isolation scopes through the tenant API.");
            var isolationScopeSelector = page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Isolation scope", Exact = true });
            await page.WaitForFunctionAsync("!document.getElementById('new-web-service-isolation-scope').disabled");
            Assert.Equal("Group default", await isolationScopeSelector.Locator("option:checked").InnerTextAsync());
            Assert.Equal(0, await page.GetByPlaceholder("MongoDB ObjectId").CountAsync());
            await CaptureWorkspaceAsync(page, name, "create");
            var serviceResponse = await page.RunAndWaitForResponseAsync(
                () => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create service", Exact = true }).ClickAsync(),
                response => response.Request.Method == HttpMethod.Post.Method &&
                            new Uri(response.Url).AbsolutePath.TrimEnd('/') == "/api/v1/web-services");
            Assert.True(serviceResponse.Ok, $"Browser image creation returned HTTP {serviceResponse.Status}.");
            var createdService = await serviceResponse.JsonAsync()
                                 ?? throw new InvalidOperationException("The browser-created service response was empty.");
            var serviceId = createdService.GetProperty("webServiceId").GetString()
                            ?? throw new InvalidOperationException("The browser-created service has no id.");
            resources.Defer(
                $"delete browser-created service '{serviceId}'",
                token => DeleteWebServiceAsync(serviceId, token));
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Service overview", Exact = true }).WaitForAsync();
            await fixture.WaitForNamespaceCreationAsync(resourceGroupId, cancellation.Token);
            await WaitForImageDeploymentAsync(resourceGroupId, serviceId, serviceName, cancellation.Token);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Refresh service details", Exact = true }).ClickAsync();
            await page.GetByText("Applied", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await CaptureWorkspaceAsync(page, name, "overview");
            await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Deployments", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Physical deployments", Exact = true }).WaitForAsync();
            Assert.Equal(0, await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Service overview", Exact = true }).CountAsync());
            await CaptureWorkspaceAsync(page, name, "deployments");
            await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Configuration", Exact = true }).ClickAsync();
            await page.GetByLabel("Environment variables").FillAsync("UI_REVIEW=true");
            await CaptureWorkspaceAsync(page, name, "configuration");
            var workspaceTabs = page.GetByRole(AriaRole.Tablist, new PageGetByRoleOptions { Name = "Web service workspace", Exact = true });
            var resourceTab = workspaceTabs.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = serviceName });
            await resourceTab.FocusAsync();
            await page.Keyboard.PressAsync("Home");
            await workspaceTabs.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "All services", Exact = true }).WaitForAsync();
            await page.Keyboard.PressAsync("End");
            Assert.Equal("UI_REVIEW=true", await page.GetByLabel("Environment variables").InputValueAsync());
            await page.Keyboard.PressAsync("Delete");
            var dirtyDialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = $"Discard changes to {serviceName}?", Exact = true });
            await dirtyDialog.WaitForAsync();
            Assert.True(await dirtyDialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cancel", Exact = true })
                .EvaluateAsync<bool>("element => element === document.activeElement"));
            await page.Keyboard.PressAsync("Escape");
            await dirtyDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
            Assert.Equal("UI_REVIEW=true", await page.GetByLabel("Environment variables").InputValueAsync());
            var updateResponse = await page.RunAndWaitForResponseAsync(
                () => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save changes", Exact = true }).ClickAsync(),
                response => response.Request.Method == HttpMethod.Put.Method && new Uri(response.Url).AbsolutePath == $"/api/v1/web-services/{serviceId}");
            Assert.True(updateResponse.Ok, $"Browser configuration update returned HTTP {updateResponse.Status}.");
            await resourceTab.WaitForAsync();
            await WaitForImageDeploymentAsync(resourceGroupId, serviceId, serviceName, cancellation.Token);
            await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Builds", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Source builds", Exact = true }).WaitForAsync();
            await CaptureWorkspaceAsync(page, name, "builds");
            await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Configuration", Exact = true }).ClickAsync();
            await page.ReloadAsync();
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Service configuration", Exact = true }).WaitForAsync();
            Assert.Equal("UI_REVIEW=true", await page.GetByLabel("Environment variables").InputValueAsync());
            await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "All services", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = serviceName, Exact = true }).WaitForAsync();
            if (previousServiceName is not null)
            {
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = previousServiceName, Exact = true }).ClickAsync();
                Assert.Equal(3, await workspaceTabs.GetByRole(AriaRole.Tab).CountAsync());
                await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Service overview", Exact = true }).WaitForAsync();
                Assert.True(await workspaceTabs.EvaluateAsync<bool>("element => { const tab = element.querySelector('[aria-selected=\"true\"]').parentElement.getBoundingClientRect(); const strip = element.getBoundingClientRect(); return tab.left >= strip.left - 1 && tab.right <= strip.right + 1; }"),
                    "The active resource tab and its close control must stay within the tab strip.");
                await resourceTab.ClickAsync();
                Assert.Equal("UI_REVIEW=true", await page.GetByLabel("Environment variables").InputValueAsync());
                await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "All services", Exact = true }).ClickAsync();
            }
            await CaptureWorkspaceAsync(page, name, "catalog");
            var deleteButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Delete {serviceName}", Exact = true });
            await deleteButton.ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = $"Delete {serviceName}?", Exact = true });
            await dialog.WaitForAsync();
            Assert.True(await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cancel", Exact = true })
                .EvaluateAsync<bool>("element => element === document.activeElement"));
            await page.Keyboard.PressAsync("Escape");
            await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
            Assert.True(await deleteButton.EvaluateAsync<bool>("element => element === document.activeElement"));
            previousServiceName = serviceName;
        }
    }

    private async Task CaptureWorkspaceAsync(IPage page, string viewport, string section)
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "frontend-tabs"));
        Directory.CreateDirectory(directory);
        foreach (var theme in new[] { "light", "dark" })
        {
            if (await page.Locator("html").GetAttributeAsync("data-theme") != theme)
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Switch to {theme} theme" }).ClickAsync();
            Assert.Equal(theme, await page.Locator("html").GetAttributeAsync("data-theme"));
            await page.EvaluateAsync("window.scrollTo(0, 0)");
            var screenshot = Path.Combine(directory, $"{viewport}-{section}-{theme}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshot, FullPage = true, Animations = ScreenshotAnimations.Disabled });
            output.WriteLine($"Frontend workspace screenshot: {screenshot}");
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"),
                $"The {viewport}/{section}/{theme} workspace overflows its viewport. Screenshot: {screenshot}");
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
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound);
        await fixture.WaitForNamespaceDeletionAsync(resourceGroupId, cancellationToken);
    }

    private async Task WaitForImageDeploymentAsync(
        string resourceGroupId,
        string serviceId,
        string serviceName,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var request = fixture.CreateAuthenticatedRequest(
                HttpMethod.Get,
                $"/api/v1/web-services/{serviceId}",
                fixture.Alpha,
                fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var placements = document.RootElement.GetProperty("deployments");
            if (placements.GetArrayLength() == 1 && placements[0].GetProperty("status").GetString() == "Applied")
            {
                var deployment = await fixture.GetWebServiceDeploymentAsync(resourceGroupId, serviceName, cancellationToken);
                Assert.Equal("docker.io/library/nginx:1.27.5-alpine",
                    deployment.GetProperty("spec").GetProperty("template").GetProperty("spec")
                        .GetProperty("containers")[0].GetProperty("image").GetString());
                if (deployment.TryGetProperty("status", out var state) &&
                    state.TryGetProperty("availableReplicas", out var replicas) && replicas.GetInt32() >= 1)
                    return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task DeleteWebServiceAsync(string serviceId, CancellationToken cancellationToken)
    {
        using (var request = fixture.CreateAuthenticatedRequest(
                   HttpMethod.Delete,
                   $"/api/v1/web-services/{serviceId}",
                   fixture.Alpha,
                   fixture.Alpha.DefaultTenantId))
        using (var response = await fixture.Api.SendAsync(request, cancellationToken))
            Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound);
        while (true)
        {
            using var request = fixture.CreateAuthenticatedRequest(
                HttpMethod.Get,
                $"/api/v1/web-services/{serviceId}",
                fixture.Alpha,
                fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return;
            response.EnsureSuccessStatusCode();
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task CaptureAuthDiagnosticAsync(
        IPage page,
        string viewport,
        string step,
        ConcurrentQueue<string> navigation)
    {
        try
        {
            output.WriteLine($"Browser auth timeout: viewport={viewport}, step={step}, path={SafePath(page.Url)}");
            foreach (var item in navigation)
                output.WriteLine($"Browser auth navigation: {item}");
            var directory = Path.Combine(".artifacts", "browser-auth-diagnostics");
            Directory.CreateDirectory(directory);
            var screenshot = Path.GetFullPath(Path.Combine(
                directory,
                $"{step}-{Guid.NewGuid():N}.png"));
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = screenshot,
                Timeout = 5000
            });
            output.WriteLine($"Browser auth screenshot: {screenshot}");
        }
        catch (Exception error)
        {
            try
            {
                output.WriteLine($"Browser auth diagnostic unavailable: {error.GetType().Name}");
            }
            catch (Exception)
            {
                // Preserve the original locator timeout if diagnostic output also fails.
            }
        }
    }

    private static string SafePath(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Path)
            : "unavailable";

}
