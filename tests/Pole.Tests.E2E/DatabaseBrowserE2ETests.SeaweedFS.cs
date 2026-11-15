using System.Net;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed partial class DatabaseBrowserE2ETests
{
    [Fact]
    public async Task Owner_can_provision_SeaweedFS_rotate_a_bucket_key_and_restore_the_bucket()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;
        var resources = new TestResourceScope("SeaweedFS browser", TimeSpan.FromMinutes(15), _timings);
        await resources.RunAsync(async () =>
        {
            var name = $"weed-{Guid.NewGuid():N}"[..18];
            var group = await CreateGroupAsync(name, ct);
            resources.Defer("delete SeaweedFS group", token => DeleteGroupAsync(group, token));
            var placement = await PrepareStorageProfileAsync("pole/seaweedfs", ct);
            resources.Defer(
                "disable SeaweedFS profile",
                token => PlatformTestProfiles.DisableStorageAsync(fixture, placement.ProfileId, token));
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.ConnectAsync(
                await fixture.GetBrowserEndpointAsync(ct),
                new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var context
                = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
            var page = await BrowserEvidence.NewPageAsync(context);
            await LoginAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "SeaweedFS", Exact = true }).ClickAsync();
            var content = page.Locator("#app-content");
            ILocator Button(string label) => content.GetByRole(AriaRole.Button, new() { Name = label, Exact = true });
            await Button("New SeaweedFS instance").ClickAsync();
            await content.GetByLabel("Resource group").SelectOptionAsync(group);
            await content.GetByLabel("Deployment target").SelectOptionAsync(placement.TargetId);
            await content.GetByLabel("Storage profile").SelectOptionAsync(placement.ProfileId);
            await content.GetByLabel("Name", new() { Exact = true }).FillAsync(name);
            var created = await page.RunAndWaitForResponseAsync(
                () => Button("Create SeaweedFS").ClickAsync(),
                r => r.Request.Method == "POST" && new Uri(r.Url).AbsolutePath.TrimEnd('/') == "/api/v1/seaweedfs");
            Assert.Equal(202, created.Status);
            var id = (await created.JsonAsync())!.Value.GetProperty("seaweedFSId").GetString()!;
            resources.Defer("purge retained SeaweedFS instance", token => PurgeBrowserSeaweedFSAsync(id, token));
            await WaitForSeaweedFSAsync(id, ct);
            await content.GetByLabel("Bucket name").FillAsync("app-objects");
            var bucketResponse = await page.RunAndWaitForResponseAsync(
                () => Button("Create bucket").ClickAsync(),
                r => r.Request.Method == "POST" && new Uri(r.Url).AbsolutePath.EndsWith(
                    $"/{id}/buckets",
                    StringComparison.Ordinal));
            Assert.True(bucketResponse.Ok);
            var bucketId = (await bucketResponse.JsonAsync())!.Value.GetProperty("id").GetString()!;
            await content.GetByRole(AriaRole.Heading, new() { Name = "app-objects", Exact = true }).WaitForAsync();
            await WaitForSeaweedFSAsync(id, ct);

            async Task Confirm(
                string action,
                string confirmation,
                string method,
                string suffix)
            {
                await Button(action).ClickAsync();
                var dialog = page.GetByRole(AriaRole.Dialog);
                var result = await page.RunAndWaitForResponseAsync(
                    () => dialog.GetByRole(AriaRole.Button, new() { Name = confirmation, Exact = true }).ClickAsync(),
                    r => r.Request.Method == method &&
                        new Uri(r.Url).AbsolutePath.Contains($"/{id}/buckets/", StringComparison.Ordinal) &&
                        new Uri(r.Url).AbsolutePath.EndsWith(suffix, StringComparison.Ordinal));
                Assert.True(result.Ok, $"Bucket action {action} returned HTTP {result.Status}.");
            }

            await using var gateway = await fixture.ForwardSeaweedFSForTestAsync(group, id, ct);

            async Task<AmazonS3Client> Client()
            {
                using var request = fixture.CreateAuthenticatedRequest(
                    HttpMethod.Get,
                    $"/api/v1/seaweedfs/{id}/buckets/{bucketId}/credentials",
                    fixture.Alpha,
                    fixture.Alpha.DefaultTenantId);
                using var result = await fixture.Api.SendAsync(request, ct);
                result.EnsureSuccessStatusCode();
                var keys = await result.Content.ReadFromJsonAsync<JsonElement>(ct);
                return new AmazonS3Client(
                    new BasicAWSCredentials(
                        keys.GetProperty("accessKey").GetString(),
                        keys.GetProperty("secretKey").GetString()),
                    new AmazonS3Config
                    {
                        ServiceURL = $"http://127.0.0.1:{gateway.LocalPort}", AuthenticationRegion = "us-east-1",
                        ForcePathStyle = true
                    });
            }

            using var initial = await Client();
            const string key = "probe.txt";
            var value = Guid.NewGuid().ToString();
            await initial.PutObjectAsync(
                new PutObjectRequest { BucketName = "app-objects", Key = key, ContentBody = value },
                ct);
            await Confirm("Rotate key", "Rotate key", "POST", "/rotate-credentials");
            await WaitForSeaweedFSAsync(id, ct);
            using var rotated = await Client();
            using (var result = await rotated.GetObjectAsync("app-objects", key, ct))
            using (var reader = new StreamReader(result.ResponseStream))
                Assert.Equal(value, await reader.ReadToEndAsync(ct));
            await TestWait.UntilAsync(
                async token =>
                {
                    try
                    {
                        using var response = await initial.GetObjectAsync("app-objects", key, token);
                        return false;
                    }
                    catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.Forbidden)
                    {
                        return true;
                    }
                },
                TimeSpan.FromMinutes(1),
                "Rotated S3 key remained accepted.",
                ct);
            await Confirm("Delete bucket", "Delete", "DELETE", "");
            await Confirm("Restore bucket", "Restore", "POST", "/restore");
            await WaitForSeaweedFSAsync(id, ct);
            using (var result = await rotated.GetObjectAsync("app-objects", key, ct))
            using (var reader = new StreamReader(result.ResponseStream))
                Assert.Equal(value, await reader.ReadToEndAsync(ct));
            await Assertions.Expect(Button("Show bucket credentials")).ToBeVisibleAsync();
        });
    }

    private Task WaitForSeaweedFSAsync(
        string id,
        CancellationToken ct,
        TimeSpan? readinessTimeout = null) => TestWait.UntilAsync(
        async token =>
        {
            using var request = fixture.CreateAuthenticatedRequest(
                HttpMethod.Get,
                $"/api/v1/seaweedfs/{id}",
                fixture.Alpha,
                fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var service = await response.Content.ReadFromJsonAsync<JsonElement>(token);
            var placement = service.GetProperty("placement");
            await AdvanceSeaweedFSAsync(placement, token);
            return placement.GetProperty("status").GetString() == "Applied" &&
                placement.GetProperty("desiredGeneration").GetInt64() ==
                placement.GetProperty("observedGeneration").GetInt64();
        },
        readinessTimeout ?? TimeSpan.FromMinutes(7),
        "SeaweedFS did not apply the current generation.",
        ct,
        pollInterval: TimeSpan.FromSeconds(3));

    private async Task AdvanceSeaweedFSAsync(JsonElement placement, CancellationToken ct)
    {
        // The E2E host holds automatic deployment retries for an hour to allow explicit retry scenarios.
        if (placement.GetProperty("status").GetString() is not ("Failed" or "DeleteFailed"))
            return;
        var code = placement.GetProperty("failureCode").GetString();
        if (code is not ("RuntimeCellNotReady" or "SeaweedFSNotReady" or "SeaweedFSBucketsPending"
            or "SeaweedFSMetadataPending" or "SeaweedFSAccessClosing" or "SeaweedFSBucketCleanup"
            or "SeaweedFSRepairCleanup" or "SeaweedFSCleanup" or "SeaweedFSMetadataCleanup" or "SeaweedFSStorageCleanup"
            or "SeaweedFSNetworkCleanup"))
            throw new InvalidOperationException($"SeaweedFS reconciliation failed with {code}.");
        var id = placement.GetProperty("deploymentId").GetString();
        var revision = placement.GetProperty("deploymentRevision").GetInt64();
        var operation = placement.GetProperty("desiredOperation").GetString();
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/v1/resource-deployments/{id}/retry?expectedRevision={revision}&expectedOperation={operation}",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        using var result = await fixture.Api.SendAsync(request, ct);
        if (result.StatusCode != HttpStatusCode.Conflict)
            result.EnsureSuccessStatusCode();
    }

    private async Task PurgeBrowserSeaweedFSAsync(string id, CancellationToken ct)
    {
        var path = $"/api/v1/seaweedfs/{id}";
        await TestWait.UntilAsync(
            async token =>
            {
                using var request = fixture.CreateAuthenticatedRequest(
                    HttpMethod.Get,
                    path,
                    fixture.Alpha,
                    fixture.Alpha.DefaultTenantId);
                using var response = await fixture.Api.SendAsync(request, token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return true;
                response.EnsureSuccessStatusCode();
                var instance = await response.Content.ReadFromJsonAsync<JsonElement>(token);
                await AdvanceSeaweedFSAsync(instance.GetProperty("placement"), token);
                if (instance.GetProperty("lifecycle").GetString() == "Active")
                {
                    var revision = instance.GetProperty("revision").GetInt64();
                    using var delete = fixture.CreateAuthenticatedRequest(
                        HttpMethod.Delete,
                        $"{path}?expectedRevision={revision}",
                        fixture.Alpha,
                        fixture.Alpha.DefaultTenantId);
                    using var accepted = await fixture.Api.SendAsync(delete, token);
                    if (accepted.StatusCode != HttpStatusCode.Conflict)
                        accepted.EnsureSuccessStatusCode();
                }

                return false;
            },
            TimeSpan.FromMinutes(14),
            "SeaweedFS was not purged by its retention job.",
            ct,
            pollInterval: TimeSpan.FromSeconds(3));
    }
}
