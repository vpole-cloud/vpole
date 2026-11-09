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
    private readonly TestTimings _timings = new(typeof(BrowserSmokeE2ETests));

    [Fact]
    public async Task User_can_manage_image_services_in_separate_workspace_tabs_at_desktop_and_mobile_widths()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var testEvidence = new TestPhaseJournal(nameof(User_can_manage_image_services_in_separate_workspace_tabs_at_desktop_and_mobile_widths)).Activate();
        var resources = new TestResourceScope("browser smoke E2E", timings: _timings);
        await resources.RunAsync(async () =>
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var setupPhase = _timings.Measure("e2e.setup");
            var tenantId = await CreateJourneyTenantAsync(cancellation.Token);
            var quotaBaseline = await ReadQuotaUsageAsync(tenantId, cancellation.Token);
            using var browserAcquisition = _timings.Measure("e2e.browser.acquire");
            var browserEndpoint = await fixture.GetBrowserEndpointAsync(cancellation.Token);
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectAsync(browserEndpoint,
                new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var browserDisposal = _timings.MeasureDisposal(browser, "e2e.browser.dispose");
            browserAcquisition.Dispose();

            var sidebarGroupName = $"aa-rename-{Guid.NewGuid():N}"[..20];
            using var sidebarRequest = fixture.CreateAuthenticatedRequest(HttpMethod.Post,
                "/api/v1/resource-groups", fixture.Alpha, tenantId);
            sidebarRequest.Content = JsonContent.Create(new
            {
                name = sidebarGroupName,
                description = "A sidebar label containing the form field name",
                tags = new[] { "e2e" }
            });
            using var sidebarResponse = await fixture.Api.SendAsync(sidebarRequest, cancellation.Token);
            Assert.True(sidebarResponse.IsSuccessStatusCode, "The selector regression group must be created.");
            var sidebarGroup = await sidebarResponse.Content.ReadFromJsonAsync<JsonElement>(cancellation.Token);
            var sidebarGroupId = sidebarGroup.GetProperty("id").GetString()!;
            resources.Defer("delete selector regression group",
                token => DeleteResourceGroupAsync(sidebarGroupId, tenantId, token));

            var servicePrefix = $"browser-{Guid.NewGuid():N}"[..16];
            string? previousServiceName = null;
            var services = new List<JourneyService>();
            setupPhase.Dispose();
            foreach (var (name, width, height) in new[]
            {
                ("desktop", 1440, 900),
                ("mobile", 390, 844)
            })
            {
                using var viewportPhase = _timings.Measure(name == "desktop" ? "e2e.image.desktop" : "e2e.image.mobile");
                var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = width, Height = height },
                    // Keycloak's local HTTPS endpoint uses an untrusted development certificate.
                    IgnoreHTTPSErrors = true
                });
                await using var contextDisposal = _timings.MeasureDisposal(context, "e2e.browser.context.dispose");
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
                await tenantSelector.SelectOptionAsync(tenantId);
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Resource groups" }).ClickAsync();
                await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Environment boundaries" }).WaitForAsync();
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New group" }).ClickAsync();
                var sidebarTree = page.GetByTestId("resource-tree");
                if (await sidebarTree.GetAttributeAsync("open") is null)
                    await sidebarTree.Locator("summary").ClickAsync();

                await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
                {
                    Name = $"Expand {sidebarGroupName}",
                    Exact = true
                })).ToBeVisibleAsync();

                var resourceGroupName = $"{name}-{Guid.NewGuid():N}"[..20];
                await content.GetByRole(AriaRole.Textbox,
                    new LocatorGetByRoleOptions { Name = "Name", Exact = true }).FillAsync(resourceGroupName);
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
                    token => DeleteResourceGroupAsync(resourceGroupId, tenantId, token));

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
                    token => DeleteWebServiceAsync(serviceId, tenantId, token));
                await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Service overview", Exact = true }).WaitForAsync();
                await fixture.WaitForNamespaceCreationAsync(resourceGroupId, cancellation.Token, tenantId: tenantId);
                await WaitForImageDeploymentAsync(resourceGroupId, serviceId, serviceName, tenantId, cancellation.Token);
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
                Assert.Contains($"tenant={tenantId}", configurationUrl);
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
                await WaitForImageDeploymentAsync(resourceGroupId, serviceId, serviceName, tenantId, cancellation.Token,
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
                catalogUrl.Query = $"view=web-services&tenant={tenantId}&resource=catalog&pageSize=1&search={servicePrefix}";
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

                var groupUrl = new UriBuilder(page.Url) { Query = $"view=resource-groups&tenant={tenantId}&resource={resourceGroupId}&section=resources" };
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
                var native = await fixture.GetWebServiceDeploymentAsync(resourceGroupId, serviceName, cancellation.Token);
                Assert.Equal(tenantId, native.GetProperty("metadata").GetProperty("labels").GetProperty("pole.io/tenant-id").GetString());
                Assert.Equal(serviceId, native.GetProperty("metadata").GetProperty("labels").GetProperty("pole.io/resource-id").GetString());
                services.Add(new JourneyService(serviceId, resourceGroupId, serviceName,
                    native.GetProperty("metadata").GetProperty("uid").GetString()!));
                previousServiceName = serviceName;
                viewportPhase.Dispose();
                if (name == "mobile")
                {
                    using (_timings.Measure("e2e.image.quota-and-recreation"))
                        await VerifyQuotaAndRecreationAsync(page, tenantId, services, quotaBaseline, resources, cancellation.Token);
                }
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

    private async Task DeleteResourceGroupAsync(string resourceGroupId, string tenantId, CancellationToken cancellationToken)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteResourceGroupAsync");
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Delete,
            $"/api/v1/resource-groups/{resourceGroupId}",
            fixture.Alpha,
            tenantId);
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound);
        await fixture.WaitForNamespaceDeletionAsync(resourceGroupId, cancellationToken);
    }

    private Task WaitForImageDeploymentAsync(
        string resourceGroupId,
        string serviceId,
        string serviceName,
        string tenantId,
        CancellationToken cancellationToken,
        long minimumRevision = 1,
        IReadOnlyDictionary<string, string>? expectedEnvironment = null) =>
        TestWait.UntilAsync(async probeToken =>
        {
            using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get,
                $"/api/v1/web-services/{serviceId}", fixture.Alpha, tenantId);
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

    private async Task DeleteWebServiceAsync(string serviceId, string tenantId, CancellationToken cancellationToken)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteWebServiceAsync");
        using (var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/v1/web-services/{serviceId}", fixture.Alpha, tenantId))
        using (var response = await fixture.Api.SendAsync(request, cancellationToken))
            if (TestDeletion.IsComplete(response.StatusCode)) return;
        await WaitForServiceDeletionAsync(serviceId, tenantId, cancellationToken);
    }

    private async Task WaitForServiceDeletionAsync(string serviceId, string tenantId, CancellationToken cancellationToken)
    {
        await TestWait.UntilAsync(async probeToken =>
        {
            using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/web-services/{serviceId}", fixture.Alpha, tenantId);
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

    private async Task<string> CreateJourneyTenantAsync(CancellationToken ct)
    {
        // Tenant creation is a platform-admin API. The actual journey still uses Alpha.
        using var request = fixture.CreatePlatformAdminRequest(HttpMethod.Post, "/api/v1/tenants");
        request.Content = JsonContent.Create(new { name = $"image-e2e-{Guid.NewGuid():N}"[..20] });
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tenant = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("Owner", tenant.GetProperty("role").GetString());
        var tenantId = tenant.GetProperty("id").GetString()!;
        using var membership = fixture.CreatePlatformAdminRequest(HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/members");
        membership.Content = JsonContent.Create(new
        {
            userId = fixture.Alpha.KeycloakUserId,
            email = fixture.Alpha.Email,
            role = "Owner"
        });
        using var granted = await fixture.Api.SendAsync(membership, ct);
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        var owner = await granted.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(tenantId, owner.GetProperty("tenantId").GetString());
        Assert.Equal(fixture.Alpha.KeycloakUserId, owner.GetProperty("userId").GetString());
        Assert.Equal("Owner", owner.GetProperty("role").GetString());
        return tenantId;
    }

    private async Task<JsonElement> ReadQuotaUsageAsync(string tenantId, CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get,
            "/api/v1/tenant-quotas/usage", fixture.Alpha, tenantId);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var usage = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(tenantId, usage.GetProperty("tenantId").GetString());
        return usage;
    }

    private static long QuotaValue(JsonElement usage, string section, string key) => usage.GetProperty(section)
        .EnumerateArray().Single(value => value.GetProperty("key").GetString() == key).GetProperty("value").GetInt64();

    private async Task AssertQuotaUsageAsync(string tenantId, JsonElement baseline, int serviceDelta, CancellationToken ct,
        bool allDimensionsAtBaseline = false)
    {
        var usage = await ReadQuotaUsageAsync(tenantId, ct);
        Assert.Equal(QuotaValue(baseline, "committedUsage", "WebServiceCount") + serviceDelta,
            QuotaValue(usage, "committedUsage", "WebServiceCount"));
        foreach (var key in new[] { "WebServiceCount", "WorkloadInstances", "CpuMillis", "MemoryMi" })
        {
            Assert.Equal(0, QuotaValue(usage, "activeReservations", key));
            if (allDimensionsAtBaseline)
                Assert.Equal(QuotaValue(baseline, "committedUsage", key), QuotaValue(usage, "committedUsage", key));
        }
    }

    private async Task AssertGroupRegistrationAsync(string tenantId, string groupId, string serviceId, bool present,
        CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get,
            $"/api/v1/resource-groups/{groupId}/resources?pageNumber=1&pageSize=50", fixture.Alpha, tenantId);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var inventory = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var matching = inventory.GetProperty("items").EnumerateArray().Where(item =>
            item.GetProperty("resourceId").GetString() == serviceId).ToArray();
        if (present)
        {
            var registration = Assert.Single(matching);
            Assert.Equal("pole/web-service", registration.GetProperty("resourceTypeId").GetString());
        }
        else
            Assert.Empty(matching);
    }

    private async Task VerifyQuotaAndRecreationAsync(IPage page, string tenantId, IReadOnlyList<JourneyService> services,
        JsonElement baseline, TestResourceScope resources, CancellationToken ct)
    {
        Assert.Equal(2, services.Count);
        var desktop = services[0];
        var mobile = services[1];
        await AssertQuotaUsageAsync(tenantId, baseline, 2, ct);
        foreach (var service in services)
            await AssertGroupRegistrationAsync(tenantId, service.GroupId, service.Id, present: true, ct);
        using (_timings.Measure("e2e.image.delete-original"))
        {
            await DeleteThroughUiAsync(page, tenantId, mobile.Name, mobile.Id, ct);
            await WaitForServiceDeletionAsync(mobile.Id, tenantId, ct);
            await fixture.WaitForOwnedImageServiceDeletionAsync(mobile.GroupId, mobile.Id, mobile.NativeUid, ct);
            await AssertGroupRegistrationAsync(tenantId, mobile.GroupId, mobile.Id, present: false, ct);
            await AssertQuotaUsageAsync(tenantId, baseline, 1, ct);
        }

        JourneyService replacement;
        using (_timings.Measure("e2e.image.recreate"))
        {
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New service", Exact = true }).ClickAsync();
            await page.GetByLabel("Name", new PageGetByLabelOptions { Exact = true }).FillAsync(mobile.Name);
            await page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Resource group" }).SelectOptionAsync(mobile.GroupId);
            await page.GetByLabel("Image registry").FillAsync("docker.io");
            await page.GetByLabel("Image name").FillAsync("library/nginx");
            await page.GetByLabel("Tag", new PageGetByLabelOptions { Exact = true }).FillAsync("1.27.5-alpine");
            await page.GetByLabel("Probe path").FillAsync("/");
            await page.GetByLabel("Probe port").FillAsync("80");
            await page.GetByLabel("Target port").FillAsync("80");
            await page.WaitForFunctionAsync("!document.getElementById('new-web-service-isolation-scope').disabled");
            var create = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create service", Exact = true });
            await Assertions.Expect(create).ToBeEnabledAsync();
            var accepted = await page.RunAndWaitForResponseAsync(() => create.ClickAsync(), response =>
                response.Request.Method == HttpMethod.Post.Method &&
                new Uri(response.Url).AbsolutePath.TrimEnd('/') == "/api/v1/web-services");
            Assert.True(accepted.Ok, $"Same-name recreation returned HTTP {accepted.Status}.");
            var body = await accepted.JsonAsync() ?? throw new InvalidOperationException("Recreation response was empty.");
            var replacementId = body.GetProperty("webServiceId").GetString()!;
            resources.Defer("delete recreated image service", cleanup => DeleteWebServiceAsync(replacementId, tenantId, cleanup));
            Assert.NotEqual(mobile.Id, replacementId);
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Service overview", Exact = true }).WaitForAsync();
            var location = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(page.Url).Query);
            Assert.Equal(replacementId, location["resource"].ToString());
            Assert.Equal(tenantId, location["tenant"].ToString());
            await WaitForImageDeploymentAsync(mobile.GroupId, replacementId, mobile.Name, tenantId, ct);
            var deployment = await fixture.GetWebServiceDeploymentAsync(mobile.GroupId, mobile.Name, ct);
            var replacementUid = deployment.GetProperty("metadata").GetProperty("uid").GetString()!;
            Assert.NotEqual(mobile.NativeUid, replacementUid);
            Assert.Equal(tenantId, deployment.GetProperty("metadata").GetProperty("labels").GetProperty("pole.io/tenant-id").GetString());
            Assert.Equal(replacementId, deployment.GetProperty("metadata").GetProperty("labels").GetProperty("pole.io/resource-id").GetString());
            replacement = new JourneyService(replacementId, mobile.GroupId, mobile.Name, replacementUid);
            await AssertGroupRegistrationAsync(tenantId, mobile.GroupId, replacement.Id, present: true, ct);
            await AssertGroupRegistrationAsync(tenantId, mobile.GroupId, mobile.Id, present: false, ct);
            await AssertQuotaUsageAsync(tenantId, baseline, 2, ct);
        }

        using (_timings.Measure("e2e.image.delete-final"))
        {
            foreach (var service in new[] { replacement, desktop })
            {
                await DeleteThroughUiAsync(page, tenantId, service.Name, service.Id, ct);
                await WaitForServiceDeletionAsync(service.Id, tenantId, ct);
                await fixture.WaitForOwnedImageServiceDeletionAsync(service.GroupId, service.Id, service.NativeUid, ct);
                await AssertGroupRegistrationAsync(tenantId, service.GroupId, service.Id, present: false, ct);
            }
            await AssertQuotaUsageAsync(tenantId, baseline, 0, ct, allDimensionsAtBaseline: true);
        }
    }

    private async Task DeleteThroughUiAsync(IPage page, string tenantId, string name, string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await page.GotoAsync(new Uri(fixture.FrontendBaseAddress,
            $"?view=web-services&tenant={tenantId}&resource=catalog").ToString());
        await page.Locator("#app-content").GetByRole(AriaRole.Button,
            new LocatorGetByRoleOptions { Name = $"Delete {name}", Exact = true }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = $"Delete {name}?", Exact = true });
        var deleted = await page.RunAndWaitForResponseAsync(() => dialog.GetByRole(AriaRole.Button,
            new LocatorGetByRoleOptions { Name = "Delete service", Exact = true }).ClickAsync(), response =>
            response.Request.Method == HttpMethod.Delete.Method && new Uri(response.Url).AbsolutePath == $"/api/v1/web-services/{id}");
        Assert.Equal(202, deleted.Status);
    }

    private sealed record JourneyService(
        string Id,
        string GroupId,
        string Name,
        string NativeUid);

}
