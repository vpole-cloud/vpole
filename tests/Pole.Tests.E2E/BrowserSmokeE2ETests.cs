using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
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
        using var testEvidence = new TestPhaseJournal(nameof(User_can_manage_image_services_in_separate_workspace_tabs_at_desktop_and_mobile_widths)).Activate();
        var resources = new TestResourceScope("browser smoke E2E");
        await resources.RunAsync(async () =>
        {
            await using var browserContainer = new PlaywrightBrowserContainer(fixture.Identity);
            using (var startup = new CancellationTokenSource(TimeSpan.FromMinutes(20)))
                await browserContainer.StartAsync(startup.Token);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.ConnectAsync(
                browserContainer.WebSocketEndpoint,
                new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });

            var servicePrefix = $"browser-{Guid.NewGuid():N}"[..16];
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
                var page = await BrowserEvidence.NewPageAsync(context);
                var content = page.Locator("#app-content");
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
                    WaitUntil = WaitUntilState.DOMContentLoaded
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
                await CaptureWorkspaceAsync(page, name, "group-create", "create");
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

                await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = resourceGroupName, Exact = true }).WaitForAsync();
                Assert.Contains($"resource={resourceGroupId}", page.Url);
                await CaptureWorkspaceAsync(page, name, "group-resources", resourceGroupId);
                await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Configuration", Exact = true }).ClickAsync();
                await page.GetByLabel("Description").FillAsync($"{name} metadata saved through group workspace");
                await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "All groups", Exact = true }).ClickAsync();
                await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = resourceGroupName }).ClickAsync();
                Assert.Equal($"{name} metadata saved through group workspace", await page.GetByLabel("Description").InputValueAsync());
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Close {resourceGroupName}", Exact = true }).ClickAsync();
                var groupDraftDialog = page.GetByRole(AriaRole.Dialog);
                await groupDraftDialog.WaitForAsync();
                await groupDraftDialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cancel", Exact = true }).ClickAsync();
                var groupSaveResponse = await page.RunAndWaitForResponseAsync(
                    () => content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save changes", Exact = true }).ClickAsync(),
                    response => response.Request.Method == HttpMethod.Put.Method && new Uri(response.Url).AbsolutePath.EndsWith($"/resource-groups/{resourceGroupId}", StringComparison.Ordinal));
                Assert.True(groupSaveResponse.Ok, "Group metadata must save through the tenant API.");
                await page.ReloadAsync();
                await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Edit resource group", Exact = true }).WaitForAsync();
                Assert.Equal($"{name} metadata saved through group workspace", await page.GetByLabel("Description").InputValueAsync());
                await CaptureWorkspaceAsync(page, name, "group-configuration", resourceGroupId);
                await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = resourceGroupName }).WaitForAsync();

                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Web services", Exact = true }).ClickAsync();
                Assert.Equal(1, await page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Tenant", Exact = true }).CountAsync());
                Assert.Equal(0, await page.GetByText("Active tenant", new PageGetByTextOptions { Exact = true }).CountAsync());
                Assert.Equal(0, await page.GetByText("Session", new PageGetByTextOptions { Exact = true }).CountAsync());
                Assert.Equal(0, await page.Locator("#app-content").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Resource groups", Exact = true }).CountAsync());
                Assert.Equal(0, await page.Locator("form:not(.inventory-search):visible").CountAsync());
                await CaptureWorkspaceAsync(page, name, "catalog", "catalog");
                var scopesResponseTask = page.WaitForResponseAsync(response => response.Request.Method == HttpMethod.Get.Method &&
                    new Uri(response.Url).AbsolutePath.EndsWith("/isolation-scopes", StringComparison.Ordinal));
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New service", Exact = true }).ClickAsync();
                var serviceName = $"{servicePrefix}-{name}";
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
                await CaptureWorkspaceAsync(page, name, "create", "create");
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
                await CaptureWorkspaceAsync(page, name, "overview", serviceId);
                await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Deployments", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Physical deployments", Exact = true }).WaitForAsync();
                Assert.Equal(0, await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Service overview", Exact = true }).CountAsync());
                await CaptureWorkspaceAsync(page, name, "deployments", serviceId);
                await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Configuration", Exact = true }).ClickAsync();
                await page.GetByLabel("Environment variables").FillAsync("UI_REVIEW=true");
                var configurationUrl = page.Url;
                Assert.Contains($"tenant={fixture.Alpha.DefaultTenantId}", configurationUrl);
                Assert.Contains($"resource={serviceId}", configurationUrl);
                Assert.Contains("section=configuration", configurationUrl);
                var beforeRenewal = await page.RunAndWaitForResponseAsync(
                    () => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Refresh service details", Exact = true }).ClickAsync(),
                    response => response.Request.Method == HttpMethod.Get.Method && new Uri(response.Url).AbsolutePath == $"/api/v1/web-services/{serviceId}");
                var priorAuthorization = (await beforeRenewal.Request.AllHeadersAsync()).GetValueOrDefault("authorization");
                // Force a real refresh through the Vite-served singleton; return no credentials.
                var refreshResponse = await page.RunAndWaitForResponseAsync(
                    async () => Assert.True(await page.EvaluateAsync<bool>("async () => { const { default: keycloak } = await import('/src/auth/keycloak.ts'); return await keycloak.updateToken(-1); }"), "Keycloak did not refresh the browser session."),
                    response => new Uri(response.Url).AbsolutePath.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal)
                        && response.Request.PostData?.Contains("grant_type=refresh_token", StringComparison.Ordinal) == true);
                Assert.True(refreshResponse.Ok, "Browser token renewal failed.");
                Assert.Equal("UI_REVIEW=true", await page.GetByLabel("Environment variables").InputValueAsync());
                Assert.Equal(configurationUrl, page.Url);
                var afterRenewal = await page.RunAndWaitForResponseAsync(
                    () => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Refresh service details", Exact = true }).ClickAsync(),
                    response => response.Request.Method == HttpMethod.Get.Method && new Uri(response.Url).AbsolutePath == $"/api/v1/web-services/{serviceId}");
                var renewedAuthorization = (await afterRenewal.Request.AllHeadersAsync()).GetValueOrDefault("authorization");
                Assert.True(!string.IsNullOrWhiteSpace(renewedAuthorization) && renewedAuthorization != priorAuthorization,
                    "The resource read did not use renewed browser credentials.");
                Assert.Equal("UI_REVIEW=true", await page.GetByLabel("Environment variables").InputValueAsync());
                await page.GoBackAsync();
                await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Physical deployments", Exact = true }).WaitForAsync();
                await page.GoForwardAsync();
                Assert.Equal("UI_REVIEW=true", await page.GetByLabel("Environment variables").InputValueAsync());
                Assert.Equal(configurationUrl, page.Url);
                await CaptureWorkspaceAsync(page, name, "configuration", serviceId);
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
                var acceptedUpdate = await updateResponse.JsonAsync() ?? throw new InvalidOperationException("Service update response was empty.");
                await WaitForImageDeploymentAsync(resourceGroupId, serviceId, serviceName, cancellation.Token,
                    acceptedUpdate.GetProperty("resourceRevision").GetInt64(), new Dictionary<string, string> { ["UI_REVIEW"] = "true" });
                await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Builds", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Source builds", Exact = true }).WaitForAsync();
                await CaptureWorkspaceAsync(page, name, "builds", serviceId);
                await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Configuration", Exact = true }).ClickAsync();
                // The URL opens this resource even without stored workspace references.
                await page.EvaluateAsync("Object.keys(localStorage).filter(key => key.startsWith('pole.web-services.workspace.')).forEach(key => localStorage.removeItem(key))");
                await page.ReloadAsync();
                await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Service configuration", Exact = true }).WaitForAsync();
                Assert.Equal("UI_REVIEW=true", await page.GetByLabel("Environment variables").InputValueAsync());
                await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "All services", Exact = true }).ClickAsync();
                await content.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = serviceName, Exact = true }).WaitForAsync();
                if (previousServiceName is not null)
                {
                    await content.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = previousServiceName, Exact = true }).ClickAsync();
                    Assert.Equal(3, await workspaceTabs.GetByRole(AriaRole.Tab).CountAsync());
                    await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Service overview", Exact = true }).WaitForAsync();
                    Assert.True(await workspaceTabs.EvaluateAsync<bool>("element => { const tab = element.querySelector('[aria-selected=\"true\"]').parentElement.getBoundingClientRect(); const strip = element.getBoundingClientRect(); return tab.left >= strip.left - 1 && tab.right <= strip.right + 1; }"),
                        "The active resource tab and its close control must stay within the tab strip.");
                    await resourceTab.ClickAsync();
                    Assert.Equal("UI_REVIEW=true", await page.GetByLabel("Environment variables").InputValueAsync());
                    await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "All services", Exact = true }).ClickAsync();
                }
                var catalogUrl = new UriBuilder(page.Url);
                catalogUrl.Query = $"view=web-services&tenant={fixture.Alpha.DefaultTenantId}&resource=catalog&pageSize=1&search={servicePrefix}";
                await page.GotoAsync(catalogUrl.Uri.ToString());
                await content.GetByText(previousServiceName is null ? "Page 1 of 1" : "Page 1 of 2", new LocatorGetByTextOptions { Exact = true }).WaitForAsync();
                if (previousServiceName is not null)
                {
                    await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Next page", Exact = true }).ClickAsync();
                    await content.GetByText("Page 2 of 2", new LocatorGetByTextOptions { Exact = true }).WaitForAsync();
                    await CaptureWorkspaceAsync(page, name, "page-two", "catalog");
                    await page.GoBackAsync();
                    await content.GetByText("Page 1 of 2", new LocatorGetByTextOptions { Exact = true }).WaitForAsync();
                    await page.GoForwardAsync();
                    await content.GetByText("Page 2 of 2", new LocatorGetByTextOptions { Exact = true }).WaitForAsync();
                }
                await page.GetByPlaceholder("Find by name or service ID").FillAsync(serviceName);
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Search", Exact = true }).ClickAsync();
                await content.GetByText("Page 1 of 1", new LocatorGetByTextOptions { Exact = true }).WaitForAsync();
                await content.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = serviceName, Exact = true }).WaitForAsync();
                Assert.Contains($"search={serviceName}", page.Url);
                await page.GoBackAsync();
                await content.GetByText(previousServiceName is null ? "Page 1 of 1" : "Page 2 of 2", new LocatorGetByTextOptions { Exact = true }).WaitForAsync();
                Assert.Equal(servicePrefix, await page.GetByPlaceholder("Find by name or service ID").InputValueAsync());
                await page.GoForwardAsync();
                await content.GetByText("Page 1 of 1", new LocatorGetByTextOptions { Exact = true }).WaitForAsync();
                Assert.Equal(serviceName, await page.GetByPlaceholder("Find by name or service ID").InputValueAsync());
                await CaptureWorkspaceAsync(page, name, "search", "catalog");
                await page.GetByPlaceholder("Find by name or service ID").FillAsync("");
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Search", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Services per page", Exact = true }).SelectOptionAsync("8");
                await content.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = serviceName, Exact = true }).WaitForAsync();
                await CaptureWorkspaceAsync(page, name, "catalog", "catalog");
                var deleteButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Delete {serviceName}", Exact = true });
                await deleteButton.ClickAsync();
                var dialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = $"Delete {serviceName}?", Exact = true });
                await dialog.WaitForAsync();
                Assert.True(await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cancel", Exact = true })
                    .EvaluateAsync<bool>("element => element === document.activeElement"));
                await page.Keyboard.PressAsync("Escape");
                await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
                Assert.True(await deleteButton.EvaluateAsync<bool>("element => element === document.activeElement"));

                var groupUrl = new UriBuilder(page.Url) { Query = $"view=resource-groups&tenant={fixture.Alpha.DefaultTenantId}&resource={resourceGroupId}&section=resources" };
                await page.GotoAsync(groupUrl.Uri.ToString());
                await content.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = serviceName, Exact = true }).WaitForAsync();
                await CaptureWorkspaceAsync(page, name, "group-resources", resourceGroupId);
                if (await page.GetByTestId("resource-tree").GetAttributeAsync("open") is null)
                    await page.GetByTestId("resource-tree").GetByText("Resources", new LocatorGetByTextOptions { Exact = true }).ClickAsync();
                var tree = page.GetByRole(AriaRole.Navigation, new PageGetByRoleOptions { Name = "Resource tree", Exact = true });
                await tree.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Expand {resourceGroupName}", Exact = true }).ClickAsync();
                await tree.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = serviceName, Exact = true }).WaitForAsync();
                await CaptureWorkspaceAsync(page, name, "resource-tree", resourceGroupId);
                await content.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = serviceName, Exact = true }).ClickAsync();
                await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Service overview", Exact = true }).WaitForAsync();
                Assert.Contains("resourceType=pole%2Fweb-service", page.Url);
                Assert.Equal(1, await page.GetByRole(AriaRole.Tablist, new PageGetByRoleOptions { Name = "Web service workspace", Exact = true })
                    .GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = serviceName, Exact = true }).CountAsync());
                await tree.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = serviceName, Exact = true }).ClickAsync();
                Assert.Equal(1, await page.GetByRole(AriaRole.Tablist, new PageGetByRoleOptions { Name = "Web service workspace", Exact = true })
                    .GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = serviceName, Exact = true }).CountAsync());
                previousServiceName = serviceName;
            }
        });
    }

    private async Task CaptureWorkspaceAsync(IPage page, string viewport, string section, string? resourceId = null)
    {
        var content = page.Locator("#app-content");
        var heading = section switch
        {
            "group-create" => "New resource group",
            "group-configuration" => "Edit resource group",
            "group-resources" or "resource-tree" => "Group resources",
            "create" => "Create a web service",
            "overview" => "Service overview",
            "deployments" => "Physical deployments",
            "configuration" => "Service configuration",
            "builds" => "Source builds",
            _ => "Web services"
        };
        await Assertions.Expect(content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = heading, Exact = true }).First).ToBeVisibleAsync();
        if (resourceId is not null)
            Assert.Equal(resourceId, Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(page.Url).Query)["resource"].ToString());
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "frontend-tabs"));
        Directory.CreateDirectory(directory);
        foreach (var theme in new[] { "light", "dark" })
        {
            if (await page.Locator("html").GetAttributeAsync("data-theme") != theme)
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Switch to {theme} theme" }).ClickAsync();
            await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", theme);
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

    private Task WaitForImageDeploymentAsync(
        string resourceGroupId,
        string serviceId,
        string serviceName,
        CancellationToken cancellationToken,
        long minimumRevision = 1,
        IReadOnlyDictionary<string, string>? expectedEnvironment = null) =>
        TestWait.UntilAsync(async probeToken =>
        {
            using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get,
                $"/api/v1/web-services/{serviceId}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, probeToken);
            response.EnsureSuccessStatusCode();
            var resource = await response.Content.ReadFromJsonAsync<JsonElement>(probeToken);
            var placements = resource.GetProperty("deployments");
            if (placements.GetArrayLength() != 1) return false;
            var placement = placements[0];
            if (resource.GetProperty("activeRevision").GetInt64() < minimumRevision ||
                placement.GetProperty("resourceRevision").GetInt64() < minimumRevision ||
                placement.GetProperty("status").GetString() != "Applied" ||
                placement.GetProperty("observedGeneration").GetInt64() != placement.GetProperty("desiredGeneration").GetInt64()) return false;
            var deployment = await fixture.GetWebServiceDeploymentAsync(resourceGroupId, serviceName, probeToken);
            JsonElement? configMap = expectedEnvironment is null
                ? null
                : await fixture.GetWebServiceConfigMapAsync(resourceGroupId, serviceName, probeToken);
            return NativeDeploymentReadiness.IsCurrent(deployment, "docker.io/library/nginx:1.27.5-alpine",
                placement.GetProperty("resourceRevision").GetInt64(), placement.GetProperty("desiredGeneration").GetInt64(),
                configMap, expectedEnvironment);
        }, TimeSpan.FromMinutes(10), "The accepted WebService rollout did not become natively ready.", cancellationToken,
            pollInterval: TimeSpan.FromSeconds(1));

    private async Task DeleteWebServiceAsync(string serviceId, CancellationToken cancellationToken)
    {
        using (var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/v1/web-services/{serviceId}", fixture.Alpha, fixture.Alpha.DefaultTenantId))
        using (var response = await fixture.Api.SendAsync(request, cancellationToken))
            if (TestDeletion.IsComplete(response.StatusCode)) return;
        await TestWait.UntilAsync(async probeToken =>
        {
            using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/web-services/{serviceId}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, probeToken);
            return TestDeletion.IsComplete(response.StatusCode);
        }, TimeSpan.FromMinutes(3), "The browser service did not finish deletion.", cancellationToken, pollInterval: TimeSpan.FromSeconds(1));
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
