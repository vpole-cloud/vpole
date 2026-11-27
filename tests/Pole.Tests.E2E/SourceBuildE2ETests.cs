// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;
using Xunit.Sdk;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
[Trait("Layer", "E2E")]
[Trait("Concern", "SourceBuild")]
[Trait("Feature", "SourceBuilds")]
[Trait("Scope", "FullE2E")]
public sealed class SourceBuildE2ETests(
    PoleE2EFixture fixture
)
{
    private const string RevisionOneCommitSha = "1111111111111111111111111111111111111111";
    private const string RevisionTwoCommitSha = "2222222222222222222222222222222222222222";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private readonly TestTimings _timings = new(typeof(SourceBuildE2ETests));

    [SourceBuildFact]
    public async Task Browser_git_creation_and_global_search_reach_the_same_source_built_resource()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var setupTiming = _timings.Measure("e2e.setup");
        using var testEvidence
            = new TestPhaseJournal(nameof(Browser_git_creation_and_global_search_reach_the_same_source_built_resource))
                .Activate();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var token = cancellation.Token;
        await fixture.EnsureSourceBuildInfrastructureReadyAsync(token);
        await EnsureGitHubConnectionAsync(token);
        var resources = new TestResourceScope("browser Git source-build E2E", timings: _timings);
        await resources.RunAsync(async () =>
        {
            resources.Defer("restore sample source revision", cleanup => SetStubRevisionAsync("v1", cleanup));
            await SetStubRevisionAsync("dependencies", token);
            var groupCleanup = resources.CreateCleanupSequence("browser Git resource group");
            var groupId = await groupCleanup.CreateAsync(
                "delete browser Git group",
                CreateResourceGroupAsync,
                (id, cleanup) => DeleteAsync($"/api/v1/resource-groups/{id}", cleanup),
                token);
            groupCleanup.Defer(
                "wait for browser Git namespace deletion",
                cleanup => fixture.WaitForNamespaceDeletionAsync(groupId, cleanup));

            string browserEndpoint;
            using (_timings.Measure("e2e.browser.acquire"))
            {
                browserEndpoint = await fixture.GetBrowserEndpointAsync(token);
            }

            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectAsync(
                browserEndpoint,
                new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var browserDisposal = _timings.MeasureDisposal(browser, "e2e.browser.dispose");
            setupTiming.Dispose();
            using var scenarioTiming = _timings.Measure("e2e.scenario");
            var context = await browser.NewContextAsync(
                new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
                    IgnoreHTTPSErrors = true,
                });
            await using var contextDisposal = _timings.MeasureDisposal(context, "e2e.browser.context.dispose");
            var page = await BrowserEvidence.NewPageAsync(context);
            await page.GotoAsync(fixture.FrontendBaseAddress.ToString());
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in", Exact = true })
                .ClickAsync();
            await page.GetByLabel("Username or email").FillAsync(fixture.Alpha.Email);
            await page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Password", Exact = true })
                .FillAsync(fixture.Alpha.Password);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true })
                .ClickAsync();
            await page.GetByLabel("Tenant").SelectOptionAsync(fixture.Alpha.DefaultTenantId);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Web services", Exact = true })
                .ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New service", Exact = true })
                .ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Git repository", Exact = true })
                .ClickAsync();
            await page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Repository" })
                .SelectOptionAsync("9001");
            await page.GetByLabel("Branch").SelectOptionAsync("main");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Analyze branch", Exact = true })
                .ClickAsync();
            var buildPlan = page.GetByLabel("Build plan");
            var candidate = await buildPlan.Locator("option").Nth(1).GetAttributeAsync("value");
            await buildPlan.SelectOptionAsync(candidate!);
            Assert.Contains(
                "mcr.microsoft.com/dotnet/aspnet",
                await page.GetByTestId("git-recipe-preview").TextContentAsync() ?? "",
                StringComparison.Ordinal);
            await page.GetByLabel("Repository technical name").FillAsync($"browser-{Guid.NewGuid():N}");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Use this source", Exact = true })
                .ClickAsync();
            var sourceId = await WaitForBrowserSourceSelectionAsync(page, token);
            var deliveryCleanup = resources.CreateCleanupSequence("browser source delivery dependencies");
            string? createdServiceId = null;
            string? createdConfigurationId = null;
            deliveryCleanup.Defer(
                "delete browser Git service",
                cleanup => createdServiceId is null
                    ? Task.CompletedTask
                    : DeleteAsync($"/api/v1/web-services/{createdServiceId}", cleanup));
            deliveryCleanup.Defer(
                "delete browser source configuration",
                cleanup => createdConfigurationId is null
                    ? Task.CompletedTask
                    : DeleteAsync($"/api/v1/source-build-configurations/{createdConfigurationId}", cleanup));
            deliveryCleanup.Defer(
                "unlink browser source",
                cleanup => DeleteAsync($"/api/v1/source-repositories/{sourceId}", cleanup));
            await page.GetByLabel("Linked source").SelectOptionAsync(sourceId);
            var name = $"git-ui-{Guid.NewGuid():N}"[..20];
            await page.GetByLabel("Build configuration technical name").FillAsync($"{name}-build");
            await page.GetByLabel("Name", new PageGetByLabelOptions { Exact = true }).FillAsync(name);
            await page.GetByLabel("Resource group").SelectOptionAsync(groupId);
            Assert.False(
                string.IsNullOrWhiteSpace(
                    await page.GetByRole(
                            AriaRole.Textbox,
                            new PageGetByRoleOptions { Name = "Dockerfile", Exact = true })
                        .InputValueAsync()));
            await page.GetByLabel("Probe path").FillAsync("/health");
            await page.GetByLabel("Probe port").FillAsync("8080");
            await page.GetByLabel("Target port").FillAsync("8080");
            await CaptureDeliveryScreenshotsAsync(page, "git-create");
            await page.SetViewportSizeAsync(1440, 900);
            var createdResponse = await page.RunAndWaitForResponseAsync(
                () => page.GetByRole(
                        AriaRole.Button,
                        new PageGetByRoleOptions { Name = "Create service and build", Exact = true })
                    .ClickAsync(),
                response => response.Request.Method == "POST" &&
                    new Uri(response.Url).AbsolutePath == "/api/v1/web-services");
            Assert.True(createdResponse.Ok, $"Git service creation returned HTTP {createdResponse.Status}.");
            var created = await createdResponse.JsonAsync() ??
                throw new InvalidOperationException("The created service was empty.");
            var serviceId = RequiredString(created, "webServiceId");
            createdServiceId = serviceId;
            await fixture.WaitForNamespaceCreationAsync(groupId, token);
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Source builds", Exact = true })
                .WaitForAsync();
            var configurationId = await GetBrowserBuildConfigurationAsync(serviceId, token);
            createdConfigurationId = configurationId;
            var buildId = await GetBrowserBuildIdAsync(serviceId, token);
            var completed = await WaitForBuildAsync(buildId, "succeeded", token);
            var state = completed.GetProperty("state");
            Assert.Equal("3333333333333333333333333333333333333333", RequiredString(completed, "commitSha"));
            var image = ImmutableImageReference(state);
            var releaseId = RequiredString(state, "releaseId");
            await AssertSourceBuildDeploymentAsync(
                groupId,
                new CreatedWebService(serviceId, name),
                image,
                releaseId,
                token);
            await fixture.WaitForWebServiceReadyAsync(groupId, name, token);
            Assert.Equal(
                RequiredString(state, "imageDigest"),
                await fixture.GetHarborArtifactDigestAsync(
                    fixture.Alpha.DefaultTenantId,
                    serviceId,
                    RequiredString(state, "imageDigest"),
                    token));
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Refresh", Exact = true })
                .Last
                .ClickAsync();
            await page.GetByText("succeeded", new PageGetByTextOptions { Exact = true }).First.WaitForAsync();
            Assert.Equal("Dockerfile", await page.GetByTestId("source-build-kind").InnerTextAsync());
            await page.Locator("[data-testid^='build-recipe-']:visible").GetByText("Build recipe ·").ClickAsync();
            var dockerfile = page.GetByRole(
                AriaRole.Textbox,
                new PageGetByRoleOptions { Name = "Dockerfile", Exact = true });
            var workingRecipe = await dockerfile.InputValueAsync();
            var missingMarker = $"pole-build-log-e2e-missing-{Guid.NewGuid():N}.txt";
            var editedRecipe = $"FROM scratch\nCOPY {missingMarker} /app/missing.txt";
            await dockerfile.FillAsync(editedRecipe);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Refresh", Exact = true })
                .Last
                .ClickAsync();
            Assert.Equal(editedRecipe, await dockerfile.InputValueAsync());
            var recipeResponse = await page.RunAndWaitForResponseAsync(
                () => page.GetByRole(
                        AriaRole.Button,
                        new PageGetByRoleOptions { Name = "Save build recipe", Exact = true })
                    .ClickAsync(),
                response => response.Request.Method == "PUT" && new Uri(response.Url).AbsolutePath ==
                    $"/api/v1/source-build-configurations/{configurationId}");
            Assert.True(recipeResponse.Ok, $"Build recipe update returned HTTP {recipeResponse.Status}.");
            await page.GetByText("Build recipe · Dockerfile", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await CaptureDeliveryScreenshotsAsync(page, "git-builds");

            await WaitForBrowserSearchProjectionAsync(serviceId, name, token);
            foreach (var (viewport, width, height) in new[] { ("desktop", 1440, 900), ("mobile", 390, 844) })
            {
                await page.SetViewportSizeAsync(width, height);
                foreach (var theme in new[] { "light", "dark" })
                {
                    await SetBrowserThemeAsync(page, theme);
                    await page.GetByRole(
                            AriaRole.Button,
                            new PageGetByRoleOptions { Name = "Search and commands", Exact = true })
                        .ClickAsync();
                    var search = page.GetByRole(
                        AriaRole.Combobox,
                        new PageGetByRoleOptions { Name = "Find a resource or action" });
                    await search.FillAsync(name);
                    await page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = name }).WaitForAsync();
                    await CaptureDeliveryScreenshotAsync(page, viewport, "global-search", theme);
                    await search.PressAsync("Enter");
                    Assert.Contains($"resource={serviceId}", page.Url);
                    Assert.Equal(
                        1,
                        await page.GetByRole(
                                AriaRole.Tablist,
                                new PageGetByRoleOptions { Name = "Web service workspace", Exact = true })
                            .GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = name, Exact = true })
                            .CountAsync());
                    await page.Keyboard.PressAsync("Control+k");
                    await page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = "Copy service link" })
                        .WaitForAsync();
                    await CaptureDeliveryScreenshotAsync(page, viewport, "commands", theme);
                    await search.PressAsync("Escape");
                    Assert.Equal(
                        "Search and commands",
                        await page.EvaluateAsync<string>("document.activeElement.getAttribute('aria-label')"));
                }
            }

            await page.SetViewportSizeAsync(1440, 900);
            await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Builds", Exact = true }).ClickAsync();
            var beforeFailure = await BuildDeploymentFingerprintAsync(groupId, name, serviceId, token);
            using (var failureBudget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                failureBudget.CancelAfter(TimeSpan.FromMinutes(3));
                var failedResponse = await page.RunAndWaitForResponseAsync(
                    () => page.GetByRole(
                            AriaRole.Button,
                            new PageGetByRoleOptions { Name = "Build and deploy", Exact = true })
                        .ClickAsync(),
                    response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath
                        .TrimEnd('/')
                        .EndsWith("/builds", StringComparison.Ordinal));
                Assert.True(failedResponse.Ok, $"Build queueing returned HTTP {failedResponse.Status}.");
                var failedJob = await failedResponse.JsonAsync() ??
                    throw new InvalidOperationException("The queued failure-test job was empty.");
                var failedId = RequiredString(failedJob, "id");
                var failed = await WaitForBuildAsync(failedId, "failed", failureBudget.Token);
                Assert.Equal("BuildKitBuildRejected", RequiredString(failed.GetProperty("state"), "failureCode"));
                var savedLog = await WaitForCompletedBuildLogAsync(
                    serviceId,
                    failedId,
                    missingMarker,
                    failureBudget.Token);
                await page.GetByText("failed", new PageGetByTextOptions { Exact = true }).First.WaitForAsync();
                Assert.Contains($"resource={serviceId}", page.Url);
                await page.GetByRole(
                        AriaRole.Button,
                        new PageGetByRoleOptions { Name = $"View logs for build {failedId}", Exact = true })
                    .ClickAsync();
                var output = page.GetByLabel("Captured build output", new PageGetByLabelOptions { Exact = true });
                await Assertions.Expect(output)
                    .ToContainTextAsync(missingMarker, new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
                await Assertions.Expect(output)
                    .ToContainTextAsync("not found", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
                Assert.Contains(
                    "Capture Completed",
                    await page.GetByTestId($"build-log-viewer-{failedId}").InnerTextAsync(),
                    StringComparison.Ordinal);
                await page.ReloadAsync();
                await Assertions.Expect(output)
                    .ToContainTextAsync(missingMarker, new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
                var reloadedLog = await WaitForCompletedBuildLogAsync(
                    serviceId,
                    failedId,
                    missingMarker,
                    failureBudget.Token);
                Assert.Equal(
                    savedLog.GetProperty("lastSequence").GetInt64(),
                    reloadedLog.GetProperty("lastSequence").GetInt64());
                await CaptureDeliveryScreenshotsAsync(page, "build-logs");
                Assert.Equal(beforeFailure, await BuildDeploymentFingerprintAsync(groupId, name, serviceId, token));
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Close logs", Exact = true })
                    .ClickAsync();
                await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Operations", Exact = true })
                    .ClickAsync();
                await page.GetByRole(
                        AriaRole.Button,
                        new PageGetByRoleOptions { Name = $"View logs for build {failedId}", Exact = true })
                    .ClickAsync();
                await Assertions.Expect(output)
                    .ToContainTextAsync(missingMarker, new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
                await CaptureDeliveryScreenshotsAsync(page, "build-operation-logs");
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Close logs", Exact = true })
                    .ClickAsync();
                await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Builds", Exact = true })
                    .ClickAsync();
                await page.Locator("[data-testid^='build-recipe-']:visible").GetByText("Build recipe ·").ClickAsync();
                await dockerfile.FillAsync(workingRecipe);
                var repaired = await page.RunAndWaitForResponseAsync(
                    () => page.GetByRole(
                            AriaRole.Button,
                            new PageGetByRoleOptions { Name = "Save build recipe", Exact = true })
                        .ClickAsync(),
                    response => response.Request.Method == "PUT" && new Uri(response.Url).AbsolutePath ==
                        $"/api/v1/source-build-configurations/{configurationId}");
                Assert.True(repaired.Ok, $"Recipe repair returned HTTP {repaired.Status}.");
                var recovery = await page.RunAndWaitForResponseAsync(
                    () => page.GetByRole(
                            AriaRole.Button,
                            new PageGetByRoleOptions { Name = "Build and deploy", Exact = true })
                        .ClickAsync(),
                    response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath
                        .TrimEnd('/')
                        .EndsWith("/builds", StringComparison.Ordinal));
                Assert.True(recovery.Ok, $"Recovery build returned HTTP {recovery.Status}.");
                var recoveryJob = await recovery.JsonAsync() ??
                    throw new InvalidOperationException("Recovery job was empty.");
                var recovered = await WaitForBuildAsync(RequiredString(recoveryJob, "id"), "succeeded", token);
                Assert.NotEqual(releaseId, RequiredString(recovered.GetProperty("state"), "releaseId"));
                await AssertSourceBuildDeploymentAsync(
                    groupId,
                    new CreatedWebService(serviceId, name),
                    ImmutableImageReference(recovered.GetProperty("state")),
                    RequiredString(recovered.GetProperty("state"), "releaseId"),
                    token);
                await fixture.WaitForWebServiceReadyAsync(groupId, name, token);
                var retainedFailure = await WaitForCompletedBuildLogAsync(serviceId, failedId, missingMarker, token);
                Assert.Equal(
                    savedLog.GetProperty("lastSequence").GetInt64(),
                    retainedFailure.GetProperty("lastSequence").GetInt64());
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Refresh", Exact = true })
                    .Last
                    .ClickAsync();
                await CaptureDeliveryScreenshotsAsync(page, "build-recovered");
            }
        });
    }

    private async Task<JsonElement> WaitForCompletedBuildLogAsync(
        string serviceId,
        string jobId,
        string marker,
        CancellationToken token)
    {
        using var timing = _timings.Measure("e2e.readiness.WaitForCompletedBuildLogAsync");
        while (true)
        {
            using var request = fixture.CreateAuthenticatedRequest(
                HttpMethod.Get,
                $"/api/v1/source-build-jobs/{jobId}/logs?webServiceId={serviceId}",
                fixture.Alpha,
                fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, token);
            Assert.True(response.IsSuccessStatusCode, $"Build log read returned HTTP {(int)response.StatusCode}.");
            var value = await response.Content.ReadFromJsonAsync<JsonElement>(token);
            if (RequiredString(value, "captureState") == "Completed")
            {
                var text = string.Concat(
                    value.GetProperty("entries").EnumerateArray().Select(entry => RequiredString(entry, "text")));
                Assert.True(
                    text.Contains(marker, StringComparison.Ordinal) &&
                    text.Contains("not found", StringComparison.Ordinal),
                    "Completed capture must contain the controlled missing-file diagnostic.");
                Assert.True(value.GetProperty("lastSequence").GetInt64() > 0);
                Assert.False(value.GetProperty("truncated").GetBoolean());
                return value;
            }

            await Task.Delay(PollInterval, token);
        }
    }

    private async Task<string> BuildDeploymentFingerprintAsync(
        string groupId,
        string name,
        string serviceId,
        CancellationToken token)
    {
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/v1/web-services/{serviceId}",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, token);
        Assert.True(response.IsSuccessStatusCode, $"Build deployment read returned HTTP {(int)response.StatusCode}.");
        var value = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        var native = await fixture.GetWebServiceDeploymentAsync(groupId, name, token);
        var template = native.GetProperty("spec").GetProperty("template");
        var releases = await GetReleasesAsync(serviceId, token);
        return JsonSerializer.Serialize(
            new
            {
                revision = value.GetProperty("activeRevision").GetInt64(),
                image = value.GetProperty("spec").GetProperty("image"),
                placements = value.GetProperty("deployments")
                    .EnumerateArray()
                    .Select(placement => new
                    {
                        id = RequiredString(placement, "deploymentId"),
                        generation = placement.GetProperty("desiredGeneration").GetInt64(),
                    })
                    .OrderBy(placement => placement.id)
                    .ToArray(),
                releases = releases.EnumerateArray().Select(release => RequiredString(release, "id")).Order().ToArray(),
                nativeImage = RequiredString(template.GetProperty("spec").GetProperty("containers")[0], "image"),
                nativeRelease = RequiredString(
                    template.GetProperty("metadata").GetProperty("annotations"),
                    "pole.server/releaseId"),
            });
    }

    private async Task<string> GetBrowserBuildConfigurationAsync(string serviceId, CancellationToken token)
    {
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Get,
            "/api/v1/source-build-configurations",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        return RequiredString(
            document.RootElement.EnumerateArray().Single(item => RequiredString(item, "webServiceId") == serviceId),
            "id");
    }

    private static async Task<string> WaitForBrowserSourceSelectionAsync(IPage page, CancellationToken token)
    {
        string? selected = null;
        await TestWait.UntilAsync(
            async phase =>
            {
                selected = await page.GetByLabel("Linked source").InputValueAsync().WaitAsync(phase);
                if (!string.IsNullOrWhiteSpace(selected))
                    return true;
                var errors = page.GetByTestId("git-source-picker").GetByRole(AriaRole.Alert);
                if (await errors.CountAsync().WaitAsync(phase) > 0)
                    throw new InvalidOperationException("Browser source setup reported an error.");
                return false;
            },
            TimeSpan.FromSeconds(30),
            "Browser linked-source selection did not complete.",
            token,
            PollInterval);
        return selected!;
    }

    private async Task<string> GetBrowserBuildIdAsync(string serviceId, CancellationToken token)
    {
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/v1/source-build-jobs?webServiceId={serviceId}",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        return RequiredString(document.RootElement.GetProperty("items").EnumerateArray().Single(), "id");
    }

    private async Task WaitForBrowserSearchProjectionAsync(
        string serviceId,
        string name,
        CancellationToken token)
    {
        using var timing = _timings.Measure("e2e.readiness.WaitForBrowserSearchProjectionAsync");
        while (true)
        {
            using var request = fixture.CreateAuthenticatedRequest(
                HttpMethod.Get,
                $"/api/v1/search?query={Uri.EscapeDataString(name)}&pageNumber=1&pageSize=20",
                fixture.Alpha,
                fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, token);
            Assert.True(response.IsSuccessStatusCode, $"Browser search returned HTTP {(int)response.StatusCode}.");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
            if (document.RootElement
                .GetProperty("items")
                .EnumerateArray()
                .Any(item => RequiredString(item, "sourceId") == serviceId))
                return;
            await Task.Delay(PollInterval, token);
        }
    }

    private static async Task SetBrowserThemeAsync(IPage page, string theme)
    {
        if (await page.Locator("html").GetAttributeAsync("data-theme") != theme)
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Switch to {theme} theme" })
                .ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", theme);
    }

    private static async Task CaptureDeliveryScreenshotsAsync(IPage page, string section)
    {
        foreach (var (viewport, width, height) in new[] { ("desktop", 1440, 900), ("mobile", 390, 844) })
        {
            await page.SetViewportSizeAsync(width, height);
            foreach (var theme in new[] { "light", "dark" })
            {
                await SetBrowserThemeAsync(page, theme);
                await CaptureDeliveryScreenshotAsync(page, viewport, section, theme);
            }
        }
    }

    private static async Task CaptureDeliveryScreenshotAsync(
        IPage page,
        string viewport,
        string section,
        string theme)
    {
        var directory = Path.GetFullPath(
            Path.Combine(
                ".artifacts",
                section.StartsWith("build-", StringComparison.Ordinal) ? "frontend-build-logs" : "frontend-90-6"));
        Directory.CreateDirectory(directory);
        await page.EvaluateAsync("window.scrollTo(0, 0)");
        await page.ScreenshotAsync(
            new PageScreenshotOptions
            {
                Path = Path.Combine(directory, $"{viewport}-{section}-{theme}.png"),
                FullPage = section is not ("global-search" or "commands"), Animations = ScreenshotAnimations.Disabled,
            });
        var overflow = await page.EvaluateAsync<string>(
            """
            () => {
                if (document.documentElement.scrollWidth <= window.innerWidth) return '';
                const rows = [...document.querySelectorAll('body *')].filter(element => {
                    if (element.getBoundingClientRect().right <= window.innerWidth + 1) return false;
                    for (let parent = element.parentElement; parent; parent = parent.parentElement)
                        if (['auto', 'scroll', 'hidden', 'clip'].includes(getComputedStyle(parent).overflowX)) return false;
                    return true;
                });
                return `document ${document.documentElement.scrollWidth}, viewport ${window.innerWidth}; ` + rows.slice(0, 16)
                    .map(element => `${element.tagName}.${element.className}: ${Math.round(element.getBoundingClientRect().right)}`).join('; ');
            }
            """);
        Assert.True(string.IsNullOrEmpty(overflow), $"The {viewport}/{section}/{theme} page overflows: {overflow}.");
    }

    [SourceBuildFact]
    public async Task Source_build_uses_the_exact_repository_commit_pushes_an_artifact_and_records_release_provenance()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var testEvidence = new TestPhaseJournal(
                nameof(Source_build_uses_the_exact_repository_commit_pushes_an_artifact_and_records_release_provenance))
            .Activate();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var cancellationToken = cancellation.Token;
        await fixture.EnsureSourceBuildInfrastructureReadyAsync(cancellationToken);
        var resources = new TestResourceScope("source-build E2E", timings: _timings);
        await resources.RunAsync(async () =>
        {
            resources.Defer("restore sample source revision", cleanup => SetStubRevisionAsync("v1", cleanup));
            await SetStubRevisionAsync("v1", cancellationToken);

            var resourceGroupCleanup = resources.CreateCleanupSequence("source-build resource group");
            var resourceGroupId = await resourceGroupCleanup.CreateAsync(
                "delete source-build resource group",
                CreateResourceGroupAsync,
                (id, token) => DeleteAsync($"/api/v1/resource-groups/{id}", token),
                cancellationToken);
            resourceGroupCleanup.Defer(
                "wait for source-build namespace deletion",
                token => fixture.WaitForNamespaceDeletionAsync(resourceGroupId, token));
            var deliveryCleanup = resources.CreateCleanupSequence("source delivery dependencies");
            var webService = await deliveryCleanup.CreateAsync(
                "delete source-build WebService",
                token => CreateWebServiceAsync(resourceGroupId, token),
                (service, token) => DeleteAsync($"/api/v1/web-services/{service.Id}", token),
                cancellationToken);
            var connectionId = await EnsureGitHubConnectionAsync(cancellationToken);
            var sourceRepositoryId = await LinkSourceRepositoryAsync(connectionId, cancellationToken);
            string? configurationId = null;
            deliveryCleanup.Defer(
                "delete source-build configuration",
                token => configurationId is null
                    ? Task.CompletedTask
                    : DeleteAsync($"/api/v1/source-build-configurations/{configurationId}", token));
            deliveryCleanup.Defer(
                "delete linked source repository",
                token => DeleteAsync($"/api/v1/source-repositories/{sourceRepositoryId}", token));
            var sourceBuildConfigurationId = await CreateSourceBuildConfigurationAsync(
                sourceRepositoryId,
                webService.Id,
                cancellationToken);
            configurationId = sourceBuildConfigurationId;
            Assert.Empty((await GetReleasesAsync(webService.Id, cancellationToken)).EnumerateArray());

            var v1Build = await StartBuildAsync(sourceBuildConfigurationId, cancellationToken);
            var v1CompletedBuild = await WaitForBuildAsync(v1Build, "succeeded", cancellationToken);
            var v1State = v1CompletedBuild.GetProperty("state");
            var v1CommitSha = RequiredString(v1CompletedBuild, "commitSha");
            var v1ImageDigest = RequiredString(v1State, "imageDigest");
            var v1ImageReference = ImmutableImageReference(v1State);
            var v1ReleaseId = RequiredString(v1State, "releaseId");
            Assert.Equal(RevisionOneCommitSha, v1CommitSha);

            var v1ArtifactDigest = await fixture.GetHarborArtifactDigestAsync(
                fixture.Alpha.DefaultTenantId,
                webService.Id,
                v1ImageDigest,
                cancellationToken);
            Assert.Equal(v1ImageDigest, v1ArtifactDigest);
            await fixture.WaitForNamespaceCreationAsync(resourceGroupId, cancellationToken);
            await WaitForReleaseOutcomeAsync(webService.Id, "Succeeded", cancellationToken, v1ReleaseId);
            var v1Releases = await GetReleasesAsync(webService.Id, cancellationToken);
            var v1Release = FindRelease(v1Releases, v1ReleaseId);
            AssertSourceBuildRelease(v1Release, v1ReleaseId, v1CommitSha, v1ImageDigest, v1ImageReference);
            await AssertSourceBuildDeploymentAsync(
                resourceGroupId,
                webService,
                v1ImageReference,
                v1ReleaseId,
                cancellationToken);

            await SetStubRevisionAsync("v2", cancellationToken);

            var v2Build = await StartBuildAsync(sourceBuildConfigurationId, cancellationToken);
            var v2CompletedBuild = await WaitForBuildAsync(v2Build, "succeeded", cancellationToken);
            var v2State = v2CompletedBuild.GetProperty("state");
            var v2CommitSha = RequiredString(v2CompletedBuild, "commitSha");
            var v2ImageDigest = RequiredString(v2State, "imageDigest");
            var v2ImageReference = ImmutableImageReference(v2State);
            var v2ReleaseId = RequiredString(v2State, "releaseId");
            Assert.Equal(RevisionTwoCommitSha, v2CommitSha);
            Assert.NotEqual(v1ImageDigest, v2ImageDigest);

            var v2ArtifactDigest = await fixture.GetHarborArtifactDigestAsync(
                fixture.Alpha.DefaultTenantId,
                webService.Id,
                v2ImageDigest,
                cancellationToken);
            Assert.Equal(v2ImageDigest, v2ArtifactDigest);
            await WaitForReleaseOutcomeAsync(webService.Id, "Succeeded", cancellationToken, v2ReleaseId);
            var releases = await GetReleasesAsync(webService.Id, cancellationToken);
            AssertSourceBuildRelease(
                FindRelease(releases, v1ReleaseId),
                v1ReleaseId,
                v1CommitSha,
                v1ImageDigest,
                v1ImageReference);
            AssertSourceBuildRelease(
                FindRelease(releases, v2ReleaseId),
                v2ReleaseId,
                v2CommitSha,
                v2ImageDigest,
                v2ImageReference);
            await AssertSourceBuildDeploymentAsync(
                resourceGroupId,
                webService,
                v2ImageReference,
                v2ReleaseId,
                cancellationToken);

            var buildKitRestoration = new TestResourceScope("source-build outage restoration", TimeSpan.FromMinutes(2));
            buildKitRestoration.Defer("restore BuildKit access", fixture.RestoreBuildKitAccessAsync);
            await buildKitRestoration.RunAsync(async () =>
            {
                await fixture.StopBuildKitAccessAsync();
                var failedBuild = await StartBuildAsync(sourceBuildConfigurationId, cancellationToken);
                var failed = await WaitForBuildAsync(failedBuild, "failed", cancellationToken);
                var failedState = failed.GetProperty("state");
                Assert.False(string.IsNullOrWhiteSpace(failedState.GetProperty("failureCode").GetString()));
                Assert.DoesNotContain(
                    "password",
                    failedState.GetProperty("failureMessage").GetString() ?? "",
                    StringComparison.OrdinalIgnoreCase);
            });
        });
    }

    private async Task<string> CreateResourceGroupAsync(CancellationToken cancellationToken)
    {
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/v1/resource-groups",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        request.Content = JsonContent.Create(
            new
            {
                name = $"sb-{Guid.NewGuid():N}"[..20],
                description = "Source-build E2E resource group",
                tags = new[] { "e2e", "source-build" },
            });
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.GetProperty("id").GetString() ??
            throw new InvalidOperationException("Pole did not return a resource group id.");
    }

    private async Task<CreatedWebService> CreateWebServiceAsync(
        string resourceGroupId,
        CancellationToken cancellationToken)
    {
        var name = $"sb-{Guid.NewGuid():N}"[..20];
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/v1/web-services",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        request.Content = JsonContent.Create(
            new
            {
                name,
                resourceGroupId,
                exposure = "Public",
                creationMode = "SourceBuild",
                tags = new[] { "e2e", "source-build" },
                spec = new
                {
                    image = (object?)null,
                    config = new
                    {
                        environmentVariables = new Dictionary<string, string>(),
                        portMappings = new[]
                            { new { servicePort = 80, targetPort = "80", protocol = "TCP", name = "http" } },
                    },
                    autoscaling = (object?)null,
                    probes = new
                    {
                        startup = Probe(),
                        liveness = Probe(),
                        readiness = Probe(),
                    },
                },
                placement = new
                {
                    isolationScopeId = (string?)null,
                    targets = new[]
                    {
                        new
                        {
                            deploymentTargetId = (string?)null,
                            fixedReplicas = 1,
                            computeProfileId = (string?)null,
                        },
                    },
                },
            });
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var id = document.RootElement.GetProperty("webServiceId").GetString() ??
            throw new InvalidOperationException("Pole did not return a web service id.");
        return new CreatedWebService(id, name);
    }

    private async Task<string> EnsureGitHubConnectionAsync(CancellationToken cancellationToken)
    {
        using var list = fixture.CreateAuthenticatedRequest(
            HttpMethod.Get,
            "/api/v1/git-provider-connections",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        using var listResponse = await fixture.Api.SendAsync(list, cancellationToken);
        listResponse.EnsureSuccessStatusCode();
        using var connections = JsonDocument.Parse(await listResponse.Content.ReadAsStreamAsync(cancellationToken));
        var existing = connections.RootElement.EnumerateArray().FirstOrDefault();
        if (existing.ValueKind != JsonValueKind.Undefined)
            return existing.GetProperty("id").GetString()!;

        using var begin = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/v1/git-provider-connections/github/install",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        begin.Content = JsonContent.Create(new { TechnicalName = "github-e2e" });
        using var beginResponse = await fixture.Api.SendAsync(begin, cancellationToken);
        beginResponse.EnsureSuccessStatusCode();
        using var beginJson = JsonDocument.Parse(await beginResponse.Content.ReadAsStreamAsync(cancellationToken));
        using var callback = await fixture.Stub.GetAsync(
            beginJson.RootElement.GetProperty("installUrl").GetString(),
            cancellationToken);
        callback.EnsureSuccessStatusCode();

        using var verifiedList = fixture.CreateAuthenticatedRequest(
            HttpMethod.Get,
            "/api/v1/git-provider-connections",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        using var verifiedResponse = await fixture.Api.SendAsync(verifiedList, cancellationToken);
        verifiedResponse.EnsureSuccessStatusCode();
        using var verified = JsonDocument.Parse(await verifiedResponse.Content.ReadAsStreamAsync(cancellationToken));
        return verified.RootElement.EnumerateArray().Single().GetProperty("id").GetString()!;
    }

    private async Task<string> LinkSourceRepositoryAsync(string connectionId, CancellationToken cancellationToken)
    {
        const string repositoryId = "9001";
        using var link = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/v1/git-provider-connections/{connectionId}/repositories/{repositoryId}/links",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        link.Content = JsonContent.Create(new { technicalName = $"source-{Guid.NewGuid():N}", branch = "main" });
        using var linkResponse = await fixture.Api.SendAsync(link, cancellationToken);
        if (!linkResponse.IsSuccessStatusCode)
        {
            var diagnostics = await fixture.GetServerErrorDiagnosticsAsync(cancellationToken);
            var stubRequests = await fixture.GetGitHubStubRequestDiagnosticsAsync(cancellationToken);
            var responseDetails = await ReadSafeErrorDetailsAsync(linkResponse, cancellationToken);
            throw new HttpRequestException(
                $"Pole could not link the analyzed source repository: {(int)linkResponse.StatusCode}." +
                (string.IsNullOrWhiteSpace(responseDetails)
                    ? ""
                    : $"{Environment.NewLine}API detail: {responseDetails}") +
                (string.IsNullOrWhiteSpace(diagnostics)
                    ? ""
                    : $"{Environment.NewLine}Server diagnostic: {diagnostics}") +
                (string.IsNullOrWhiteSpace(stubRequests)
                    ? ""
                    : $"{Environment.NewLine}GitHub stub requests:{Environment.NewLine}{stubRequests}"),
                null,
                linkResponse.StatusCode);
        }

        linkResponse.EnsureSuccessStatusCode();
        using var source = JsonDocument.Parse(await linkResponse.Content.ReadAsStreamAsync(cancellationToken));
        return source.RootElement.GetProperty("id").GetString() ??
            throw new InvalidOperationException("Pole did not return a source repository id.");
    }

    private async Task SetStubRevisionAsync(string revision, CancellationToken cancellationToken)
    {
        using var response = await fixture.Stub.PostAsJsonAsync("/__e2e/revision", new { revision }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> CreateSourceBuildConfigurationAsync(
        string sourceRepositoryId,
        string webServiceId,
        CancellationToken cancellationToken)
    {
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/v1/source-build-configurations",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        request.Content = JsonContent.Create(
            new
            {
                technicalName = $"build-{Guid.NewGuid():N}",
                sourceRepositoryId,
                webServiceId,
                recipe = new { dockerfile = "FROM traefik/whoami:v1.11.0\nCOPY src/Program.cs /app/revision.txt\n" },
            });
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var configuration = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return configuration.RootElement.GetProperty("id").GetString() ??
            throw new InvalidOperationException("Pole did not return a source build configuration id.");
    }

    private async Task<string> StartBuildAsync(string configurationId, CancellationToken cancellationToken)
    {
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/v1/source-build-configurations/{configurationId}/builds",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var job = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return job.RootElement.GetProperty("id").GetString() ??
            throw new InvalidOperationException("Pole did not return a source build id.");
    }

    private async Task<JsonElement> WaitForBuildAsync(
        string buildJobId,
        string status,
        CancellationToken cancellationToken)
    {
        using var timing = _timings.Measure("e2e.readiness.WaitForBuildAsync");
        while (true)
        {
            using var request = fixture.CreateAuthenticatedRequest(
                HttpMethod.Get,
                $"/api/v1/source-build-jobs/{buildJobId}",
                fixture.Alpha,
                fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var job = document.RootElement.Clone();
            var currentStatus = job.GetProperty("state").GetProperty("status").GetString();
            if (string.Equals(currentStatus, status, StringComparison.OrdinalIgnoreCase))
                return job;
            if (string.Equals(currentStatus, "failed", StringComparison.OrdinalIgnoreCase))
            {
                var failedState = job.GetProperty("state");
                var failureCode = failedState.GetProperty("failureCode").GetString();
                var failureMessage = failedState.GetProperty("failureMessage").GetString();
                var diagnostics = await fixture.GetServerErrorDiagnosticsAsync(cancellationToken);
                throw new XunitException(
                    $"Expected build '{buildJobId}' to succeed, but it failed. " +
                    $"Code: {failureCode ?? "<none>"}. " + $"Message: {SanitizeDiagnostic(failureMessage)}" +
                    (string.IsNullOrWhiteSpace(diagnostics)
                        ? ""
                        : $"{Environment.NewLine}Server diagnostic: {diagnostics}"));
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private async Task WaitForReleaseOutcomeAsync(
        string webServiceId,
        string expectedOutcome,
        CancellationToken cancellationToken,
        string? releaseId = null)
    {
        using var timing = _timings.Measure("e2e.readiness.WaitForReleaseOutcomeAsync");
        while (true)
        {
            var releases = await GetReleasesAsync(webServiceId, cancellationToken);
            foreach (var release in releases.EnumerateArray())
            {
                if (releaseId is not null && !string.Equals(
                        release.GetProperty("id").GetString(),
                        releaseId,
                        StringComparison.Ordinal))
                    continue;

                var outcome = release.GetProperty("outcome").GetString();
                if (outcome == expectedOutcome)
                    return;
                if (outcome == "Failed")
                {
                    var failureMessage = release.GetProperty("failureMessage").GetString();
                    var diagnostics = await fixture.GetWebServiceDeploymentDiagnosticsAsync(cancellationToken);
                    throw new XunitException(
                        $"Web service release failed before it reached {expectedOutcome}: {SanitizeDiagnostic(failureMessage)}" +
                        (string.IsNullOrWhiteSpace(diagnostics)
                            ? ""
                            : $"{Environment.NewLine}Server diagnostic: {diagnostics}"));
                }
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private async Task<JsonElement> GetReleasesAsync(string webServiceId, CancellationToken cancellationToken)
    {
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/v1/web-services/{webServiceId}/releases",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.GetProperty("releases").GetProperty("items").Clone();
    }

    private async Task AssertSourceBuildDeploymentAsync(
        string resourceGroupId,
        CreatedWebService webService,
        string expectedImageReference,
        string expectedReleaseId,
        CancellationToken cancellationToken)
    {
        var deployment = await fixture.GetWebServiceDeploymentAsync(
            resourceGroupId,
            webService.Name,
            cancellationToken);
        var podTemplate = deployment.GetProperty("spec").GetProperty("template");
        var podSpec = podTemplate.GetProperty("spec");
        Assert.Equal(expectedImageReference, RequiredString(podSpec.GetProperty("containers")[0], "image"));
        Assert.Equal(
            expectedReleaseId,
            RequiredString(podTemplate.GetProperty("metadata").GetProperty("annotations"), "pole.server/releaseId"));

        var pullSecrets = podSpec.GetProperty("imagePullSecrets");
        Assert.True(
            pullSecrets.GetArrayLength() > 0,
            "The source-built Deployment should reference its Harbor pull secret.");
        var pullSecretName = RequiredString(pullSecrets[0], "name");
        Assert.True(
            await fixture.KubernetesSecretExistsAsync(resourceGroupId, pullSecretName, cancellationToken),
            "The source-built Deployment should have its Harbor pull secret in the resource-group namespace.");
    }

    private static string ImmutableImageReference(JsonElement state)
    {
        var reference = RequiredString(state, "imageReference");
        var digest = RequiredString(state, "imageDigest");
        var separator = reference.IndexOf('@');
        if (separator >= 0)
            return reference[..separator] + "@" + digest;

        var tag = reference.LastIndexOf(':');
        return (tag > reference.LastIndexOf('/') ? reference[..tag] : reference) + "@" + digest;
    }

    private static JsonElement FindRelease(JsonElement releases, string releaseId)
    {
        var release = releases.EnumerateArray()
            .SingleOrDefault(item => string.Equals(
                item.GetProperty("id").GetString(),
                releaseId,
                StringComparison.Ordinal));
        Assert.NotEqual(JsonValueKind.Undefined, release.ValueKind);
        return release;
    }

    private static void AssertSourceBuildRelease(
        JsonElement release,
        string expectedReleaseId,
        string expectedCommitSha,
        string expectedImageDigest,
        string expectedImageReference)
    {
        Assert.Equal(expectedReleaseId, RequiredString(release, "id"));
        Assert.Equal("SourceBuild", RequiredString(release, "action"));
        Assert.Equal("Succeeded", RequiredString(release, "outcome"));
        var provenance = release.GetProperty("provenance");
        Assert.Equal("PoleSourceBuild", RequiredString(provenance, "source"));
        Assert.Equal(expectedCommitSha, RequiredString(provenance, "commitSha"));
        Assert.Equal(expectedImageDigest, RequiredString(provenance, "imageDigest"));
        Assert.Equal(expectedImageReference, RequiredString(provenance, "newImageTag"));
    }

    private async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteAsync");
        while (true)
        {
            using var request = fixture.CreateAuthenticatedRequest(
                HttpMethod.Delete,
                path,
                fixture.Alpha,
                fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                return;
            if (response.StatusCode != HttpStatusCode.Conflict)
                response.EnsureSuccessStatusCode();

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private static object Probe() => new
    {
        path = "/",
        port = "8080",
        initialDelaySeconds = 0,
        periodSeconds = 5,
        timeoutSeconds = 3,
        failureThreshold = 3,
        successThreshold = 1,
    };

    private static string SanitizeDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<none>";

        var redacted = Regex.Replace(
            Regex.Replace(
                Regex.Replace(value, "(?i)(authorization)\\s*[:=]\\s*(?:Basic|Bearer)\\s+\\S+", "$1=[redacted]"),
                "(?i)Bearer\\s+\\S+",
                "Bearer [redacted]"),
            "(?i)(password|token|secret)(?:[\\\"']\\s*)?[:=]\\s*(?:[\\\"'])?[^\\s,}\\]\\\"']+",
            "$1=[redacted]");
        return redacted.Length <= 1_000 ? redacted : $"{redacted[..1_000]}…";
    }

    private static async Task<string> ReadSafeErrorDetailsAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType is not "application/json")
            return "";

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var properties = new[] { "message", "serviceName", "details" }
            .Where(property => document.RootElement.TryGetProperty(property, out var value) &&
                value.ValueKind == JsonValueKind.String)
            .Select(property =>
                $"{property}={SanitizeDiagnostic(document.RootElement.GetProperty(property).GetString())}");
        return string.Join(", ", properties);
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString() ??
        throw new InvalidOperationException($"Pole did not return '{propertyName}'.");

    private sealed record CreatedWebService(
        string Id,
        string Name
    );
}
