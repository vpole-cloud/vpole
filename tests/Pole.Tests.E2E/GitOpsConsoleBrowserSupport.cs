using System.Net;
using System.Text.Json;
using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed partial class GitOpsRepositoryFlowE2ETests
{
    private static ILocator ConsoleSource(IPage page, string id) => page.GetByTestId($"gitops-source-{id}");

    private static ILocator ConsoleButton(ILocator owner, string name) => owner.GetByRole(
        AriaRole.Button,
        new LocatorGetByRoleOptions { Name = name, Exact = true });

    private static Task ConsoleSectionAsync(
        IPage page,
        string id,
        string section) => ConsoleSource(page, id)
        .GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = section, Exact = true })
        .ClickAsync();

    private async Task<IPage> ConsoleLoginAsync(
        IBrowserContext context,
        TestUser user,
        string tenant)
    {
        var page = await BrowserEvidence.NewPageAsync(context);
        await page.GotoAsync(fixture.FrontendBaseAddress.ToString());
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in", Exact = true }).ClickAsync();
        await page.GetByLabel("Username or email").FillAsync(user.Email);
        await page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Password", Exact = true })
            .FillAsync(user.Password);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true }).ClickAsync();
        await page.GetByLabel("Tenant").SelectOptionAsync(tenant);
        return page;
    }

    private Task OpenConsoleSourceAsync(IPage page, string source) => page.GotoAsync(
        new Uri(
            fixture.FrontendBaseAddress,
            $"?view=gitops&resource={source}&section=overview&tenant={fixture.Alpha.DefaultTenantId}").ToString());

    private static async Task RefreshConsoleSourceAsync(IPage page, string source)
    {
        var response = await page.RunAndWaitForResponseAsync(
            () => ConsoleButton(ConsoleSource(page, source), "Refresh source").ClickAsync(),
            item => item.Request.Method == "GET" && new Uri(item.Url).AbsolutePath.TrimEnd('/') == $"{Root}/{source}");
        Assert.Equal(200, response.Status);
        await response.FinishedAsync();
    }

    private static async Task<IResponse> ConsoleCommandAsync(
        IPage page,
        string source,
        string action)
    {
        var label = action switch
        {
            "retry" => "Retry run",
            "delete" => "Delete source",
            _ => char.ToUpperInvariant(action[0]) + action[1..],
        };
        await ConsoleButton(ConsoleSource(page, source), label).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        var response = await page.RunAndWaitForResponseAsync(
            () => ConsoleButton(dialog, "Confirm " + action).ClickAsync(),
            item => item.Request.Method == (action == "delete" ? "DELETE" : "POST") &&
                new Uri(item.Url).AbsolutePath.TrimEnd('/') ==
                Root + "/" + source + (action == "delete" ? "" : "/" + action));
        Assert.Contains(response.Status, new[] { 200, 202, 204 });
        return response;
    }

    private async Task PublishConsoleAsync(
        string commit,
        IReadOnlyList<RepositoryExampleFile> files,
        CancellationToken ct)
    {
        using var response = await fixture.Stub.PostAsJsonAsync("/__e2e/gitops-console", new { commit, files }, ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<string> CreateConsoleSourceAsync(string connection, CancellationToken ct)
    {
        var created = await JsonAsync(
            HttpMethod.Post,
            Root,
            new
            {
                idempotencyKey = Guid.NewGuid(),
                provider = "GitHub",
                connectionId = connection,
                externalRepositoryId = "9006",
                branch = "main",
                manifestDirectory = "console",
                pollIntervalSeconds = 86400,
                prune = "Retain",
            },
            ct);
        return created.GetProperty("id").GetString()!;
    }

    private static IReadOnlyList<RepositoryExampleFile> ConsoleGroupFiles(string name, string directory = "console") =>
        RepositoryExampleFiles.Read("src/features/GitOps/examples/minimal/manifests", directory)
            .Where(file => file.Path.EndsWith("/resource-group.yaml", StringComparison.Ordinal))
            .Select(file => file with
            {
                Content = file.Content.Replace("gitops-minimal-group", name, StringComparison.Ordinal),
            })
            .ToArray();

    private static async Task CaptureConsoleAsync(IPage page, string screen)
    {
        var directory = Path.Combine(RepositoryPaths.FindRoot(), ".artifacts", "frontend-gitops");
        Directory.CreateDirectory(directory);
        foreach (var (viewport, width, height) in new[] { ("desktop", 1440, 900), ("mobile", 390, 844) })
        {
            await page.SetViewportSizeAsync(width, height);
            foreach (var theme in new[] { "light", "dark" })
            {
                if (await page.Locator("html").GetAttributeAsync("data-theme") != theme)
                    await page.GetByRole(
                            AriaRole.Button,
                            new PageGetByRoleOptions { Name = $"Switch to {theme} theme", Exact = true })
                        .ClickAsync();

                await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", theme);
                Assert.True(
                    await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"),
                    "The GitOps workspace must fit the viewport.");
                if (screen == "overview")
                {
                    var commits = page.Locator("#app-content [data-testid^='gitops-source-'] dl dd code:visible");
                    Assert.True(await commits.CountAsync() > 0, "Overview must render its commit progress.");
                    Assert.True(
                        await commits.EvaluateAllAsync<bool>(
                            """
                            nodes => nodes.every(node => {
                              const range = document.createRange();
                              range.selectNodeContents(node);
                              return [...range.getClientRects()].every(rect =>
                                rect.left >= -1 && rect.right <= document.documentElement.clientWidth + 1);
                            })
                            """),
                        "Every displayed commit must fit inside the viewport without clipping.");
                }

                if (screen == "protected-removal" && viewport == "mobile")
                    Assert.True(
                        await page.EvaluateAsync<bool>(
                            """
                            () => {
                                const region = document.querySelector('[aria-label="Lifecycle plan actions"]');
                                if (!region || region.scrollWidth <= region.clientWidth) return false;
                                const cells = [...region.querySelectorAll('tbody td:first-child')];
                                return cells.length > 0 && cells.every(cell => {
                                    const range = document.createRange();
                                    range.selectNodeContents(cell);
                                    return range.getClientRects().length === 1;
                                });
                            }
                            """),
                        "Plan actions must remain readable within the horizontally scrollable mobile table.");

                await page.ScreenshotAsync(
                    new PageScreenshotOptions
                    {
                        Path = Path.Combine(directory, $"gitops-{screen}-{viewport}-{theme}.png"),
                        FullPage = true,
                        Animations = ScreenshotAnimations.Disabled,
                    });
            }
        }

        await page.SetViewportSizeAsync(1440, 900);
    }

    private static async Task<JsonElement> ConsoleWriteAsync(
        IPage page,
        ILocator button,
        string path)
    {
        var response = await page.RunAndWaitForResponseAsync(
            () => button.ClickAsync(),
            item => item.Request.Method == "POST" && new Uri(item.Url).AbsolutePath.TrimEnd('/') == path);
        Assert.Contains(response.Status, new[] { 200, 201, 202 });
        var headers = await response.Request.AllHeadersAsync();
        Assert.True(
            headers.TryGetValue("authorization", out var authorization) &&
            authorization.StartsWith("Bearer ", StringComparison.Ordinal),
            "Browser writes require real authentication.");
        Assert.True(headers.ContainsKey("x-pole-tenant-id"), "Every browser write must carry its active tenant.");
        return (await response.JsonAsync())!.Value;
    }

    private static void AssertConsoleSafePayload(JsonElement payload, string forbidden)
    {
        var serialized = payload.GetRawText();
        Assert.False(
            serialized.Contains(forbidden, StringComparison.Ordinal),
            "GitOps metadata must exclude secret plaintext.");
        foreach (var field in new[] { "rawYaml", "acceptedSpec", "dockerfile", "credentials", "plaintext" })
            Assert.DoesNotContain('"' + field + '"', serialized, StringComparison.OrdinalIgnoreCase);
    }
}
