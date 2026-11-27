// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Text.Json;
using Microsoft.Playwright;
using Pole.Features.Core.Multitenancy;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.E2E;

public sealed partial class GitOpsRepositoryFlowE2ETests
{
    [Fact]
    public async Task BrowserGitOpsSourceRunsSettingsAndTenantAccess()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var setupTiming = _timings.Measure("e2e.setup");
        using var evidence = new TestPhaseJournal(nameof(BrowserGitOpsSourceRunsSettingsAndTenantAccess)).Activate();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var ct = budget.Token;
        var connection = await ConnectAsync(ct);
        const string name = "console-basic-group";
        var invalidCommit = new string('a', 39) + "1";
        var validCommit = new string('a', 39) + "2";
        var files = ConsoleGroupFiles(name);
        await PublishConsoleAsync(
            invalidCommit,
            files.Select(file => file with { Content = file.Content + "  unknownConsoleField: true\n" }).ToArray(),
            ct);

        string browserEndpoint;
        using (_timings.Measure("e2e.browser.acquire"))
        {
            browserEndpoint = await fixture.GetBrowserEndpointAsync(ct);
        }

        using var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.ConnectAsync(
            browserEndpoint,
            new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
        setupTiming.Dispose();
        using var scenarioTiming = _timings.Measure("e2e.scenario");
        await using var browserDisposal = _timings.MeasureDisposal(browser, "e2e.browser.dispose");
        var context = await browser.NewContextAsync(
            new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
                IgnoreHTTPSErrors = true,
            });
        await using var contextDisposal = _timings.MeasureDisposal(context, "e2e.browser.context.dispose");
        var page = await ConsoleLoginAsync(context, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "GitOps", Exact = true }).ClickAsync();
        var content = page.Locator("#app-content");
        await ConsoleButton(content, "New source").ClickAsync();
        await Assertions.Expect(content.GetByLabel("Polling interval (seconds)")).ToHaveValueAsync("300");
        await Assertions.Expect(content.GetByLabel("Prune policy")).ToHaveValueAsync("Retain");
        await content.GetByLabel("GitHub connection").SelectOptionAsync(connection);
        await content.GetByLabel("Manifest repository").SelectOptionAsync("9006");
        await content.GetByLabel("Branch", new LocatorGetByLabelOptions { Exact = true }).SelectOptionAsync("main");
        await content.GetByLabel("Manifest directory").FillAsync("console");
        await content.GetByLabel("Polling interval (seconds)").FillAsync("86400");
        await CaptureConsoleAsync(page, "create");
        var created = await ConsoleWriteAsync(page, ConsoleButton(content, "Create source"), Root);
        var source = created.GetProperty("id").GetString()!;
        await ConsoleSource(page, source).WaitForAsync();
        var failed = await WaitAsync(source, item => item.GetProperty("status").GetString() == "Failed", ct);
        var originalRun = LatestRunId(failed);
        Assert.Equal(invalidCommit, failed.GetProperty("synchronization").GetProperty("observedCommit").GetString());
        await RefreshConsoleSourceAsync(page, source);

        // Retry retains the resolved input even after the branch has advanced; Sync reads its current head.
        await PublishConsoleAsync(validCommit, files, ct);
        await Assertions.Expect(ConsoleButton(ConsoleSource(page, source), "Retry run")).ToBeEnabledAsync();
        var retryResponse = await ConsoleCommandAsync(page, source, "retry");
        var retryBody = JsonSerializer.Deserialize<JsonElement>(retryResponse.Request.PostData!);
        Assert.Equal(originalRun, retryBody.GetProperty("runId").GetString());
        var retried = await WaitAsync(
            source,
            item => LatestRunId(item) != originalRun && item.GetProperty("status").GetString() == "Failed",
            ct);
        var retry = await JsonAsync(HttpMethod.Get, $"{Root}/{source}/runs/{LatestRunId(retried)}", null, ct);
        Assert.Equal(invalidCommit, retry.GetProperty("commit").GetString());
        Assert.Equal(originalRun, retry.GetProperty("retryOfRunId").GetString());
        await RefreshConsoleSourceAsync(page, source);
        await ConsoleCommandAsync(page, source, "sync");
        var applied = await AppliedAsync(source, validCommit, ct);
        await RefreshConsoleSourceAsync(page, source);
        await Assertions.Expect(ConsoleSource(page, source)).ToContainTextAsync("ConfigurationApplied");
        await CaptureConsoleAsync(page, "overview");
        var sourceTab = content.Locator($"#gitops-workspace-tab-{source}");
        await Assertions.Expect(sourceTab).ToHaveAttributeAsync("aria-selected", "true");
        await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "All GitOps sources", Exact = true })
            .ClickAsync();
        await Assertions.Expect(sourceTab).ToHaveAttributeAsync("aria-selected", "false");
        await ConsoleButton(content, "Refresh sources").ClickAsync();
        var catalogRow = content.GetByTestId($"resource-row-{source}");
        await Assertions.Expect(
                catalogRow.GetByRole(AriaRole.Cell, new LocatorGetByRoleOptions { Name = "Active", Exact = true }))
            .ToBeVisibleAsync();
        await Assertions.Expect(
                catalogRow.GetByRole(
                    AriaRole.Cell,
                    new LocatorGetByRoleOptions { Name = "ConfigurationApplied", Exact = true }))
            .ToBeVisibleAsync();
        await Assertions.Expect(
                catalogRow.GetByRole(AriaRole.Cell, new LocatorGetByRoleOptions { Name = "1 / 0", Exact = true }))
            .ToBeVisibleAsync();
        await Assertions.Expect(catalogRow).ToContainTextAsync(validCommit);
        await Assertions.Expect(catalogRow.GetByRole(AriaRole.Link)).ToContainTextAsync("main/console");
        var sourceHref = await catalogRow.GetByRole(AriaRole.Link).GetAttributeAsync("href");
        Assert.Contains("view=gitops", sourceHref, StringComparison.Ordinal);
        Assert.Contains("resource=" + source, sourceHref, StringComparison.Ordinal);
        await CaptureConsoleAsync(page, "catalog");
        await catalogRow.GetByRole(AriaRole.Link).ClickAsync();
        await Assertions.Expect(sourceTab).ToHaveAttributeAsync("aria-selected", "true");
        Assert.Equal(1, await sourceTab.CountAsync());
        await Assertions
            .Expect(
                ConsoleSource(page, source)
                    .GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Overview", Exact = true }))
            .ToHaveAttributeAsync("aria-selected", "true");
        var inventory = await JsonAsync(HttpMethod.Get, $"{Root}/{source}/resources?pageSize=50", null, ct);
        var resource = Assert.Single(inventory.GetProperty("items").EnumerateArray());
        var group = resource.GetProperty("resourceId").GetString()!;
        await ConsoleSectionAsync(page, source, "Resources");
        var row = ConsoleSource(page, source).GetByTestId($"gitops-resource-{group}");
        await Assertions.Expect(row).ToContainTextAsync(name);
        await Assertions.Expect(row).ToContainTextAsync("Previously created");
        await Assertions.Expect(row).ToContainTextAsync("Git managed");
        await CaptureConsoleAsync(page, "resources");
        await row.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = name, Exact = true }).ClickAsync();
        await content.GetByRole(
                AriaRole.Link,
                new LocatorGetByRoleOptions { Name = "Open managing source", Exact = true })
            .ClickAsync();
        await ConsoleSource(page, source).WaitForAsync();
        Assert.Contains("view=gitops", page.Url, StringComparison.Ordinal);
        await ConsoleSectionAsync(page, source, "Runs");
        var run = LatestRunId(applied);
        await ConsoleButton(ConsoleSource(page, source).GetByTestId($"gitops-run-{run}"), run).ClickAsync();
        await ConsoleSource(page, source).GetByTestId($"gitops-run-detail-{run}").WaitForAsync();
        Assert.Contains("run=" + run, page.Url, StringComparison.Ordinal);
        await page.ReloadAsync();
        await ConsoleSource(page, source).GetByTestId($"gitops-run-detail-{run}").WaitForAsync();
        await CaptureConsoleAsync(page, "runs");

        await ConsoleSectionAsync(page, source, "Settings");
        var settings = ConsoleSource(page, source);
        await settings.GetByLabel("Polling interval (seconds)").FillAsync("82000");
        var current = await JsonAsync(HttpMethod.Get, $"{Root}/{source}", null, ct);
        await JsonAsync(
            HttpMethod.Put,
            $"{Root}/{source}",
            new
            {
                expectedRevision = current.GetProperty("revision").GetInt64(),
                pollIntervalSeconds = 83000,
                prune = "Retain",
            },
            ct);
        await RefreshConsoleSourceAsync(page, source);
        await Assertions.Expect(settings.GetByLabel("Polling interval (seconds)")).ToHaveValueAsync("82000");
        await Assertions.Expect(ConsoleButton(settings, "Save settings")).ToBeDisabledAsync();
        await ConsoleButton(settings, "Review and reapply").ClickAsync();
        var saved = await page.RunAndWaitForResponseAsync(
            () => ConsoleButton(settings, "Save settings").ClickAsync(),
            response => response.Request.Method == "PUT" && new Uri(response.Url).AbsolutePath == Root + "/" + source);
        Assert.Equal(200, saved.Status);
        Assert.Equal(
            82000,
            JsonSerializer.Deserialize<JsonElement>(saved.Request.PostData!)
                .GetProperty("pollIntervalSeconds")
                .GetInt32());
        await RefreshConsoleSourceAsync(page, source);
        await Assertions.Expect(settings.GetByLabel("Polling interval (seconds)")).ToHaveValueAsync("82000");
        await Assertions.Expect(settings.GetByLabel("Prune policy")).ToHaveValueAsync("Retain");
        await Assertions.Expect(ConsoleButton(settings, "Save settings")).ToBeDisabledAsync();
        await CaptureConsoleAsync(page, "settings");
        await ConsoleSectionAsync(page, source, "Overview");
        await ConsoleCommandAsync(page, source, "pause");
        await WaitAsync(source, item => item.GetProperty("activity").GetString() == "Paused", ct);
        await RefreshConsoleSourceAsync(page, source);
        await Assertions.Expect(ConsoleButton(ConsoleSource(page, source), "Sync")).ToBeDisabledAsync();
        await ConsoleCommandAsync(page, source, "resume");
        await WaitAsync(
            source,
            item => item.GetProperty("activity").GetString() == "Active" &&
                item.GetProperty("status").GetString() == "AlreadyCurrent",
            ct);
        await RefreshConsoleSourceAsync(page, source);
        await ConsoleCommandAsync(page, source, "pause");

        var member = await fixture.InviteAndJoinAsync(
            fixture.Alpha.DefaultTenantId,
            fixture.Alpha,
            fixture.Beta,
            TenantRole.Viewer,
            ct);
        try
        {
            await using var viewerContext
                = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
            var viewer = await ConsoleLoginAsync(viewerContext, fixture.Beta, fixture.Alpha.DefaultTenantId);
            await OpenConsoleSourceAsync(viewer, source);
            await Assertions.Expect(ConsoleSource(viewer, source))
                .ToContainTextAsync("Tenant viewer access is read-only");
            await Assertions.Expect(ConsoleButton(ConsoleSource(viewer, source), "Resume")).ToBeDisabledAsync();
            await ConsoleSectionAsync(viewer, source, "Previews");
            await Assertions.Expect(ConsoleButton(ConsoleSource(viewer, source), "Build lifecycle preview"))
                .ToBeDisabledAsync();
            await viewer.GetByLabel("Tenant").SelectOptionAsync(fixture.Beta.DefaultTenantId);
            await viewer.GotoAsync(
                new Uri(
                    fixture.FrontendBaseAddress,
                    $"?view=gitops&resource={source}&tenant={fixture.Beta.DefaultTenantId}").ToString());
            await Assertions.Expect(viewer.Locator("#app-content"))
                .ToContainTextAsync("Source unavailable in this tenant.");
            using var foreign = fixture.CreateAuthenticatedRequest(
                HttpMethod.Get,
                $"{Root}/{source}/resources",
                fixture.Beta,
                fixture.Beta.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(foreign, ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            using var request = fixture.CreateAuthenticatedRequest(
                HttpMethod.Delete,
                $"/api/v1/tenants/{fixture.Alpha.DefaultTenantId}/members/{member.GetProperty("id").GetString()}",
                fixture.Alpha,
                fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }
    }
}
