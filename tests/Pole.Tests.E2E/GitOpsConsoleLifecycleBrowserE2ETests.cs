using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed partial class GitOpsRepositoryFlowE2ETests
{
    private const string ConsoleAdoptedGroup = GitOpsConsoleFixtureNames.AdoptedGroup;
    private const string ConsoleAdoptedWeb = "console-adopted-web";
    private const string ConsoleAdoptedSecret = "console-adopted-secret";
    private const string ConsoleAdoptedBinding = "console-adopted-binding";

    [Fact]
    public async Task BrowserGitOpsLifecycleReviewsExactPlansAndRestoresManualControl()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var evidence = new TestPhaseJournal(
            nameof(BrowserGitOpsLifecycleReviewsExactPlansAndRestoresManualControl)).Activate();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(18));
        var ct = budget.Token;
        fixture.EnableGitOpsNamespaceCleanup();
        var connection = await ConnectAsync(ct);
        const string profile = "console-adoption-profile";
        var target = await CreateMixedCatalogAsync(ct, profile);
        var value = "console-value-" + Guid.NewGuid().ToString("N");
        var identities = await CreateConsoleManualInventoryAsync(profile, value, ct);
        var adoptedCommit = new string('b', 39) + "1";
        await PublishConsoleAsync(adoptedCommit, ConsoleAdoptionFiles(target, profile), ct);
        var source = await CreateConsoleSourceAsync(connection, ct);
        var blocked = await WaitAsync(source, item => item.GetProperty("status").GetString() == "Blocked", ct);
        await LifecycleActivityAsync(source, "pause", blocked, ct);

        var container = new PlaywrightBrowserContainer(fixture.Identity);
        await using var containerDisposal = _timings.MeasureDisposal(container, "e2e.browser.container.dispose");
        await container.StartAsync(ct);
        using var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.ConnectAsync(container.WebSocketEndpoint,
            new() { ExposeNetwork = "<loopback>" });
        await using var browserDisposal = _timings.MeasureDisposal(browser, "e2e.browser.dispose");
        var context = await browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1440, Height = 900 },
            IgnoreHTTPSErrors = true
        });
        await using var contextDisposal = _timings.MeasureDisposal(context, "e2e.browser.context.dispose");
        var page = await ConsoleLoginAsync(context, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        await OpenConsoleSourceAsync(page, source);
        await ConsoleSectionAsync(page, source, "Previews");
        var workspace = ConsoleSource(page, source);
        await workspace.GetByLabel("Lifecycle intent").SelectOptionAsync("Adoption");
        foreach (var (kind, name, id) in new[]
        {
            ("ResourceGroup", ConsoleAdoptedGroup, identities.Group),
            ("WebService", ConsoleAdoptedWeb, identities.Web),
            ("Secret", ConsoleAdoptedSecret, identities.Secret)
        })
        {
            await workspace.GetByLabel("Resource kind", new() { Exact = true }).SelectOptionAsync(kind);
            await workspace.GetByLabel("Existing technical name").FillAsync(name);
            await ConsoleButton(workspace, "Find adoption candidates").ClickAsync();
            await ConsoleButton(workspace.GetByTestId($"gitops-candidate-{id}"), "Select candidate").ClickAsync();
        }

        await workspace.GetByLabel("Resource kind", new() { Exact = true }).SelectOptionAsync("SecretBinding");
        await workspace.GetByLabel("Secret technical name").FillAsync(ConsoleAdoptedSecret);
        await workspace.GetByLabel("Consumer kind", new() { Exact = true }).SelectOptionAsync("WebService");
        await workspace.GetByLabel("Consumer technical name").FillAsync(ConsoleAdoptedWeb);
        await workspace.GetByLabel("Binding declaration name in Git").FillAsync(ConsoleAdoptedBinding);
        var candidates = await page.RunAndWaitForResponseAsync(
            () => ConsoleButton(workspace, "Find adoption candidates").ClickAsync(),
            response => response.Request.Method == "GET" &&
                new Uri(response.Url).AbsolutePath == $"{Root}/{source}/adoption-candidates" &&
                response.Url.Contains("relatedName=", StringComparison.Ordinal));
        Assert.Equal(200, candidates.Status);
        var bindingCandidate = Assert.Single((await candidates.JsonAsync())!.Value
            .GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, bindingCandidate.GetProperty("name").ValueKind);
        Assert.Equal(identities.Binding, bindingCandidate.GetProperty("resourceId").GetString());
        AssertConsoleSafePayload(bindingCandidate, value);
        await ConsoleButton(workspace.GetByTestId($"gitops-candidate-{identities.Binding}"), "Select candidate")
            .ClickAsync();
        var adoptedPreview = await BuildConsolePreviewAsync(page, source);
        AssertConsoleSafePayload(adoptedPreview, value);
        var adoptionActions = adoptedPreview.GetProperty("actions").GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("action").GetString() == "Adopt").ToArray();
        Assert.Equal(4, adoptionActions.Length);
        await ConfirmConsolePreviewAsync(page, source, adoptedPreview);
        JsonElement applied;
        try
        {
            applied = await LifecycleAppliedAsync(source, adoptedCommit, ct);
        }
        catch (Xunit.Sdk.XunitException)
        {
            var failed = await JsonAsync(HttpMethod.Get, $"{Root}/{source}", null, ct);
            var metadata = await fixture.WriteGitOpsLifecycleFailureMetadataAsync(source, LatestRunId(failed), ct);
            throw new Xunit.Sdk.XunitException("BrowserAdoptionLifecycleFailed; metadata=" + metadata);
        }

        Assert.Equal(4, applied.GetProperty("inventory").GetProperty("owned").GetInt64());
        var secret = await JsonAsync(HttpMethod.Get, "/api/v1/secrets/" + identities.Secret, null, ct);
        Assert.Equal(identities.SecretVersion, secret.GetProperty("currentVersion").GetInt64());
        AssertConsoleSafePayload(secret, value);
        await JsonAsync(HttpMethod.Get, "/api/v1/web-services/" + identities.Web, null, ct);

        // Confirmation authority expires on an owner fence change even if source/inventory metadata is unchanged.
        var removalCommit = new string('b', 39) + "2";
        await PublishConsoleAsync(removalCommit, ConsoleGroupFiles(ConsoleAdoptedGroup), ct);
        await OpenNewConsolePreviewAsync(page, source, "ResourceChanges");
        await workspace.GetByLabel("Declarations absent from the snapshot").SelectOptionAsync("Remove");
        var stale = await BuildConsolePreviewAsync(page, source);
        var previewId = stale.GetProperty("id").GetString()!;
        var review = workspace.GetByTestId($"gitops-preview-{previewId}");
        await ConsoleButton(review, "Review confirmation").ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        var acknowledgment = dialog.GetByLabel("I understand this plan may permanently delete data.");
        await Assertions.Expect(ConsoleButton(dialog, "Confirm lifecycle plan")).ToBeDisabledAsync();
        await acknowledgment.CheckAsync();
        await fixture.AdvanceConsoleSecretReferenceFenceAsync(identities.Secret, ct);
        var rejected = await page.RunAndWaitForResponseAsync(
            () => ConsoleButton(dialog, "Confirm lifecycle plan").ClickAsync(),
            response => response.Request.Method == "POST" &&
                new Uri(response.Url).AbsolutePath == $"{Root}/{source}/previews/{previewId}/confirm");
        Assert.Equal(409, rejected.Status);
        await Assertions.Expect(ConsoleButton(review, "Review confirmation")).ToBeDisabledAsync();
        await Assertions.Expect(review).ToContainTextAsync("Build and review a new preview");
        await ConsoleButton(review, "Build a new preview").ClickAsync();
        await workspace.GetByLabel("Declarations absent from the snapshot").SelectOptionAsync("Remove");
        var removal = await BuildConsolePreviewAsync(page, source);
        Assert.NotEqual(previewId, removal.GetProperty("id").GetString());
        await workspace.GetByTestId($"gitops-preview-{removal.GetProperty("id").GetString()}").WaitForAsync();
        await CaptureConsoleAsync(page, "protected-removal");
        await ConfirmConsolePreviewAsync(page, source, removal, assertAcknowledgmentReset: true);
        var removed = await LifecycleAppliedAsync(source, removalCommit, ct);
        Assert.Equal(1, removed.GetProperty("inventory").GetProperty("owned").GetInt64());

        const string renamed = GitOpsConsoleFixtureNames.RenamedGroup;
        var renameCommit = new string('b', 39) + "3";
        await PublishConsoleAsync(renameCommit, ConsoleGroupFiles(renamed), ct);
        await OpenNewConsolePreviewAsync(page, source, "Replacement");
        await workspace.GetByLabel($"Select ResourceGroup/{ConsoleAdoptedGroup}").CheckAsync();
        await workspace.GetByLabel($"Desired name for ResourceGroup/{ConsoleAdoptedGroup}").FillAsync(renamed);
        var rename = await BuildConsolePreviewAsync(page, source);
        var replacement = Assert.Single(rename.GetProperty("actions").GetProperty("items").EnumerateArray(),
            action => action.GetProperty("action").GetString() == "Replace");
        var replacementId = replacement.GetProperty("replacement").GetProperty("resourceId").GetString()!;
        Assert.NotEqual(identities.Group, replacementId);
        await ConfirmConsolePreviewAsync(page, source, rename);
        await LifecycleAppliedAsync(source, renameCommit, ct);
        await ExpectLifecycleStatusAsync(HttpMethod.Get, "/api/v1/resource-groups/" + identities.Group,
            null, HttpStatusCode.NotFound, ct);

        var relocationCommit = new string('b', 39) + "4";
        await PublishConsoleAsync(relocationCommit,
            ConsoleGroupFiles(renamed).Concat(ConsoleGroupFiles(renamed, "moved")).ToArray(), ct);
        await OpenNewConsolePreviewAsync(page, source, "Relocation");
        await workspace.GetByLabel("GitHub connection").SelectOptionAsync(connection);
        await workspace.GetByLabel("Manifest repository").SelectOptionAsync("9006");
        await workspace.GetByLabel("Branch", new() { Exact = true }).SelectOptionAsync("main");
        await workspace.GetByLabel("Manifest directory").FillAsync("moved");
        var relocation = await BuildConsolePreviewAsync(page, source);
        Assert.Equal("moved", relocation.GetProperty("input").GetProperty("repository")
            .GetProperty("manifestDirectory").GetString());
        await ConfirmConsolePreviewAsync(page, source, relocation);
        var relocated = await LifecycleAppliedAsync(source, relocationCommit, ct);
        Assert.Equal("moved", relocated.GetProperty("repository").GetProperty("manifestDirectory").GetString());
        await ConsoleSectionAsync(page, source, "Overview");
        await RefreshConsoleSourceAsync(page, source);
        await Assertions.Expect(ConsoleButton(workspace, "Delete source")).ToBeDisabledAsync();
        await ExpectLifecycleStatusAsync(HttpMethod.Delete,
            $"{Root}/{source}?expectedRevision={relocated.GetProperty("revision").GetInt64()}", null,
            HttpStatusCode.Conflict, ct);

        using (var denied = await fixture.Stub.PostAsJsonAsync("/__e2e/gitops-console/access",
                   new { denied = true }, ct))
            Assert.Equal(HttpStatusCode.NoContent, denied.StatusCode);
        try
        {
            // Inventory-only detach remains available with the repository inaccessible.
            await OpenNewConsolePreviewAsync(page, source, "Detach");
            Assert.Equal(0, await workspace.GetByLabel("Manifest repository").CountAsync());
            var detach = await BuildConsolePreviewAsync(page, source);
            Assert.Equal("Inventory", detach.GetProperty("input").GetProperty("kind").GetString());
            Assert.Equal(JsonValueKind.Null, detach.GetProperty("commit").ValueKind);
            await ConfirmConsolePreviewAsync(page, source, detach);
            var detached = await WaitAsync(source, item => item.GetProperty("activity").GetString() == "Detached", ct);
            Assert.Equal(0, detached.GetProperty("inventory").GetProperty("owned").GetInt64());
            await JsonAsync(HttpMethod.Put, "/api/v1/resource-groups/" + replacementId,
                new
                {
                    description = "Manual control restored through browser detach",
                    tags = new[] { "detached" }
                }, ct);
            await ConsoleSectionAsync(page, source, "Overview");
            await RefreshConsoleSourceAsync(page, source);
            await Assertions.Expect(workspace).ToContainTextAsync("Source is detached");
            await ConsoleCommandAsync(page, source, "delete");
            await ExpectLifecycleStatusAsync(HttpMethod.Get, $"{Root}/{source}", null, HttpStatusCode.NotFound, ct);
        }
        finally
        {
            using var restored = await fixture.Stub.PostAsJsonAsync("/__e2e/gitops-console/access",
                new { denied = false }, ct);
            restored.EnsureSuccessStatusCode();
        }
    }

    private static async Task OpenNewConsolePreviewAsync(IPage page, string source, string intent)
    {
        await ConsoleSectionAsync(page, source, "Previews");
        var workspace = ConsoleSource(page, source);
        var reset = ConsoleButton(workspace, "Build a new preview");
        if (await reset.CountAsync() > 0)
            await reset.ClickAsync();

        // Refresh metadata before selecting concrete inventory IDs for another command.
        await RefreshConsoleSourceAsync(page, source);
        await workspace.GetByLabel("Lifecycle intent").SelectOptionAsync(intent);
        var reviewedRevision = ConsoleButton(workspace, "Use reviewed source revision");
        if (await reviewedRevision.CountAsync() > 0)
            await reviewedRevision.ClickAsync();
    }

    private static Task<JsonElement> BuildConsolePreviewAsync(IPage page, string source) =>
        ConsoleWriteAsync(page, ConsoleButton(ConsoleSource(page, source), "Build lifecycle preview"),
            $"{Root}/{source}/previews");

    private static async Task ConfirmConsolePreviewAsync(IPage page, string source, JsonElement preview,
        bool assertAcknowledgmentReset = false)
    {
        var id = preview.GetProperty("id").GetString()!;
        var review = ConsoleSource(page, source).GetByTestId($"gitops-preview-{id}");
        await ConsoleButton(review, "Review confirmation").ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        var acknowledgment = dialog.GetByLabel("I understand this plan may permanently delete data.");
        if (await acknowledgment.CountAsync() > 0)
        {
            if (assertAcknowledgmentReset)
                await Assertions.Expect(acknowledgment).Not.ToBeCheckedAsync();

            await acknowledgment.CheckAsync();
        }
        else
        {
            Assert.False(assertAcknowledgmentReset, "Protected deletion must require a new acknowledgment.");
        }

        var response = await page.RunAndWaitForResponseAsync(
            () => ConsoleButton(dialog, "Confirm lifecycle plan").ClickAsync(),
            item => item.Request.Method == "POST" &&
                new Uri(item.Url).AbsolutePath == $"{Root}/{source}/previews/{id}/confirm");
        Assert.Equal(202, response.Status);
        var body = JsonSerializer.Deserialize<JsonElement>(response.Request.PostData!);
        Assert.Equal(["expectedRevision"], body.EnumerateObject().Select(property => property.Name));
        Assert.Equal(preview.GetProperty("revision").GetInt64(), body.GetProperty("expectedRevision").GetInt64());
    }
}
