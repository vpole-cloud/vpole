using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Playwright;
using Pole.Features.Core.Kubernetes;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class RuntimeLogsBrowserE2ETests(PoleE2EFixture fixture)
{
    private readonly TestTimings _timings = new(typeof(RuntimeLogsBrowserE2ETests));

    [KubernetesFact("multi-cluster")]
    public async Task Runtime_logs_select_owned_containers_stop_on_replacement_and_isolate_transport_outage()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var testEvidence = new TestPhaseJournal(nameof(Runtime_logs_select_owned_containers_stop_on_replacement_and_isolate_transport_outage)).Activate();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(10)); var ct = budget.Token;
        var lab = fixture.LabState.GetRequiredTarget("workload-a");
        var nativeConfiguration = lab.CreateConfiguration(); var originalServer = new Uri(nativeConfiguration.Host);
        await using var relay = new KubernetesApiRelay(originalServer);
        using var native = new k8s.Kubernetes(nativeConfiguration);
        var resources = new TestResourceScope("runtime diagnostics browser E2E", timings: _timings);
        Exception? primaryFailure = null;
        TestPhaseJournal? journal = null;
        try
        {
            using var setupTiming = _timings.Measure("e2e.setup");
            var kubeconfig = await File.ReadAllTextAsync(lab.KubeconfigPath, ct);
            var replacedConfig = kubeconfig.Replace(originalServer.AbsoluteUri.TrimEnd('/'), relay.Endpoint.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal);
            Assert.True(replacedConfig != kubeconfig, "The dedicated target kubeconfig server must be replaced without changing TLS identity.");
            var target = await SendAsync(HttpMethod.Post, "/api/v1/platform/deployment-targets", new
            {
                technicalName = "runtime-logs-target",
                displayName = "Runtime log test target",
                enabled = true,
                acceptsWorkloads = true,
                workloadConfiguration = new { ingress = new { ingressClassName = "traefik", clusterIssuerName = "pole-lab", publicBaseDomain = lab.PublicBaseDomain }, networkSources = new { entries = new Dictionary<string, object>() } },
                connection = new { kind = "Kubeconfig", kubeconfig = replacedConfig, contextName = lab.Context }
            }, ct, admin: true);
            var targetId = Text(target, "id");
            resources.Defer("delete runtime target", cleanup => DeleteAsync($"/api/v1/platform/deployment-targets/{targetId}", cleanup, admin: true), "cleanup.target.delete");
            await fixture.AllowTargetWithoutEnforcementAsync(targetId, target.GetProperty("revision").GetInt64(), ct);
            var targets = await SendAsync(HttpMethod.Get, "/api/v1/platform/deployment-targets", null, ct, admin: true);
            var defaultTarget = targets.EnumerateArray().Single(item => item.GetProperty("isDefault").GetBoolean()); var defaultTargetId = Text(defaultTarget, "id");
            var compute = await SendAsync(HttpMethod.Post, "/api/v1/platform/compute-profiles/", new
            {
                technicalName = "runtime-logs-compute",
                displayName = "Runtime logs test compute",
                description = "Ephemeral two-target diagnostics profile",
                resources = new { requests = new { cpuMillis = 50, memoryMi = 64 }, limits = new { cpuMillis = 250, memoryMi = 256 } },
                architecture = "Any",
                extendedResources = Array.Empty<object>(),
                allowedResourceTypes = new[] { "pole/redis" },
                tenantAccess = new { kind = "all-tenants", tenantIds = Array.Empty<string>() },
                targetMappings = new[] { TargetMapping(targetId), TargetMapping(defaultTargetId) },
                isEnabled = true,
                isDefault = false
            }, ct, admin: true);
            var computeId = Text(compute, "id");
            resources.Defer("delete runtime compute profile", cleanup => DeleteAsync($"/api/v1/platform/compute-profiles/{computeId}?revision={compute.GetProperty("revision").GetInt64()}", cleanup, admin: true), "cleanup.compute.delete");
            var storage = await SendAsync(HttpMethod.Post, "/api/v1/platform/storage/profiles", StorageBody(true, 0), ct, admin: true);
            var storageId = Text(storage, "id");
            resources.Defer("disable runtime test storage profile", async cleanup =>
            {
                var profiles = await SendAsync(HttpMethod.Get, "/api/v1/platform/storage/profiles", null, cleanup, admin: true);
                var current = profiles.EnumerateArray().Single(item => Text(item, "id") == storageId);
                await SendAsync(HttpMethod.Put, $"/api/v1/platform/storage/profiles/{storageId}", StorageBody(false, current.GetProperty("revision").GetInt64()), cleanup, admin: true);
            }, "cleanup.storage.disable");
            var suffix = Guid.NewGuid().ToString("N")[..10]; var groupName = $"runtime-{suffix}";
            var group = await SendAsync(HttpMethod.Post, "/api/v1/resource-groups", new { name = groupName, description = "Runtime logs E2E", tags = new[] { "e2e" } }, ct);
            var groupId = Text(group, "id");
            var namespaces = new Dictionary<string, string>();
            resources.Defer("delete runtime group and owned namespaces", async cleanup =>
            {
                await DeleteAsync($"/api/v1/resource-groups/{groupId}", cleanup);
                // At most the two dedicated target namespaces exist, and neither depends on the other.
                await Task.WhenAll(namespaces.Select(async pair =>
                {
                    using var namespaceTiming = _timings.Measure("cleanup.namespace.wait-deleted");
                    using var client = pair.Key == targetId ? new k8s.Kubernetes(lab.CreateConfiguration()) : new k8s.Kubernetes(fixture.LabState.Targets.Single(item => item.Key == "default").CreateConfiguration());
                    while (true)
                    {
                        try { await client.CoreV1.ReadNamespaceAsync(pair.Value, cancellationToken: cleanup); }
                        catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.NotFound) { break; }
                        await Task.Delay(500, cleanup);
                    }
                }));
            }, "cleanup.group.delete-and-wait");
            var redisCleanup = resources.CreateConcurrentCleanupGroup("delete independent runtime Redis services");
            var first = await CreateRedisAsync($"runtime-a-{suffix}", targetId, storageId, computeId, groupId, ct);
            redisCleanup.Defer("delete relay target Redis", cleanup => DeleteRedisAsync(Text(first, "redisId"), cleanup), "cleanup.redis.relay");
            var healthy = await CreateRedisAsync($"runtime-b-{suffix}", defaultTargetId, storageId, computeId, groupId, ct);
            redisCleanup.Defer("delete healthy target Redis", cleanup => DeleteRedisAsync(Text(healthy, "redisId"), cleanup), "cleanup.redis.healthy");
            resources.Defer("restore test target transport before cleanup", _ => { relay.Resume(); return Task.CompletedTask; }, "cleanup.transport.restore");
            using var readinessTiming = _timings.Measure("e2e.workloads.ready");
            var firstPlacement = await WaitForAppliedAsync(Text(first, "redisId"), ct);
            var healthyPlacement = await WaitForAppliedAsync(Text(healthy, "redisId"), ct);
            while (namespaces.Count != 2)
            {
                var cells = await SendAsync(HttpMethod.Get, $"/api/v1/resource-groups/{groupId}/runtime-cells", null, ct);
                foreach (var cell in cells.EnumerateArray().Where(item => Text(item, "state") == "ready")) namespaces[Text(cell, "deploymentTargetId")] = Text(cell, "kubernetesNamespace");
                if (namespaces.Count != 2) await Task.Delay(500, ct);
            }
            var ns = namespaces[targetId];
            var nativeNamespace = await native.CoreV1.ReadNamespaceAsync(ns, cancellationToken: ct);
            Assert.Equal(fixture.Identity.WorktreeId, nativeNamespace.Metadata.Labels[TestRunIdentity.WorktreeLabel]);
            Assert.Equal(fixture.Identity.RunId, nativeNamespace.Metadata.Labels[TestRunIdentity.RunLabel]);
            Assert.Equal(TestRunIdentity.Owner, nativeNamespace.Metadata.Labels[TestRunIdentity.OwnerLabel]);
            readinessTiming.Dispose();
            using var logContainersTiming = _timings.Measure("e2e.log-containers.ready");
            var name = $"runtime-a-{suffix}";
            var workload = await native.AppsV1.ReadNamespacedStatefulSetAsync(name, ns, cancellationToken: ct);
            var identity = new WorkloadIdentityLabels(fixture.Alpha.DefaultTenantId, groupId, Text(firstPlacement, "isolationScopeId"), Text(firstPlacement, "runtimeCellId"), targetId, "Redis", Text(first, "redisId"), Text(firstPlacement, "deploymentId"));
            Assert.True(KubernetesWorkloadObservation.Owns(workload.Metadata, identity));
            var markerA = $"runtime-log-a-{suffix}"; var markerB = $"runtime-log-b-{suffix}";
            var controllerUid = workload.Metadata.Uid;
            while (true)
            {
                workload = await native.AppsV1.ReadNamespacedStatefulSetAsync(name, ns, cancellationToken: ct);
                Assert.Equal(controllerUid, workload.Metadata.Uid);
                Assert.True(KubernetesWorkloadObservation.Owns(workload.Metadata, identity));
                var image = workload.Spec.Template.Spec.Containers.Single(item => item.Name == "redis").Image;
                foreach (var (container, marker) in new[] { ("log-a", markerA), ("log-b", markerB) })
                {
                    if (!workload.Spec.Template.Spec.Containers.Any(item => item.Name == container))
                        workload.Spec.Template.Spec.Containers.Add(new V1Container { Name = container, Image = image, Command = ["/bin/sh", "-c", $"i=0; while true; do echo '{marker} '$i; i=$((i+1)); sleep 1; done"] });
                }
                try { await native.AppsV1.ReplaceNamespacedStatefulSetAsync(workload, name, ns, cancellationToken: ct); break; }
                catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.Conflict) { await Task.Delay(500, ct); }
            }
            var discovered = await WaitForInstancesAsync(Text(firstPlacement, "deploymentId"), null, ct);
            var pod = discovered.GetProperty("diagnostics").GetProperty("instances").EnumerateArray().Single(item => item.GetProperty("containers").EnumerateArray().Any(container => Text(container, "name") == "log-a"));
            var uid = Text(pod, "uid"); var podName = Text(pod, "name");
            logContainersTiming.Dispose();
            setupTiming.Dispose();
            using var browserSetupTiming = _timings.Measure("e2e.browser.setup");
            var browserContainer = new PlaywrightBrowserContainer(fixture.Identity);
            await using var containerDisposal = _timings.MeasureDisposal(browserContainer, "e2e.browser.container.dispose");
            await browserContainer.StartAsync(ct);
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectAsync(browserContainer.WebSocketEndpoint, new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var browserDisposal = _timings.MeasureDisposal(browser, "e2e.browser.dispose");
            var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1440, Height = 900 }, IgnoreHTTPSErrors = true });
            await using var contextDisposal = _timings.MeasureDisposal(context, "e2e.browser.context.dispose");
            var page = await BrowserEvidence.NewPageAsync(context); await LoginAsync(page);
            browserSetupTiming.Dispose();
            using var scenarioTiming = _timings.Measure("e2e.scenario");
            journal = TestPhaseJournal.Current!;
            await page.GotoAsync(ResourceUrl(Text(first, "redisId")));
            await SelectAsync(page, Text(firstPlacement, "deploymentId"), uid, "log-a");
            await StartReadAsync(page, Text(firstPlacement, "deploymentId"), uid, "log-a", false, journal, ct);
            await Assertions.Expect(page.GetByLabel("Runtime log output")).ToContainTextAsync(markerA);
            Assert.DoesNotContain(markerB, await page.GetByLabel("Runtime log output").InnerTextAsync());
            await CaptureAsync(page, "recent");
            await page.GetByLabel("Container", new PageGetByLabelOptions { Exact = true }).SelectOptionAsync("log-b");
            var followed = await StartReadAsync(page, Text(firstPlacement, "deploymentId"), uid, "log-b", true, journal, ct);
            await Assertions.Expect(page.GetByLabel("Runtime log output")).ToContainTextAsync(markerB);
            Assert.DoesNotContain(markerA, await page.GetByLabel("Runtime log output").InnerTextAsync());
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            page.RequestFailed += (_, request) => { if (ReferenceEquals(request, followed)) canceled.TrySetResult(); };
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Close runtime diagnostics", Exact = true }).ClickAsync();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            await SelectAsync(page, Text(firstPlacement, "deploymentId"), uid, "log-a");
            await StartReadAsync(page, Text(firstPlacement, "deploymentId"), uid, "log-a", true, journal, ct);
            await Assertions.Expect(page.GetByLabel("Runtime log output")).ToContainTextAsync(markerA);
            var selectedPod = await native.CoreV1.ReadNamespacedPodAsync(podName, ns, cancellationToken: ct);
            Assert.Equal(uid, selectedPod.Metadata.Uid);
            Assert.True(KubernetesWorkloadObservation.Owns(selectedPod.Metadata, identity));
            Assert.Contains(selectedPod.Metadata.OwnerReferences, owner => owner.Controller == true && owner.Uid == workload.Metadata.Uid);
            await native.CoreV1.DeleteNamespacedPodAsync(podName, ns, body: new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = uid }, GracePeriodSeconds = 0 }, cancellationToken: ct);
            await Assertions.Expect(page.GetByTestId($"runtime-log-viewer-{Text(firstPlacement, "deploymentId")}")).ToContainTextAsync("selected instance", new LocatorAssertionsToContainTextOptions { Timeout = 20_000 });
            var replaced = await WaitForInstancesAsync(Text(firstPlacement, "deploymentId"), uid, ct);
            var replacement = replaced.GetProperty("diagnostics").GetProperty("instances").EnumerateArray().Single(item => Text(item, "name") == podName);
            Assert.NotEqual(uid, Text(replacement, "uid"));
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Refresh instances and events", Exact = true }).ClickAsync();
            await page.GetByLabel("Instance", new PageGetByLabelOptions { Exact = true }).SelectOptionAsync(Text(replacement, "uid"));
            await page.GetByLabel("Container", new PageGetByLabelOptions { Exact = true }).SelectOptionAsync("log-a");
            journal.Record(TestPhase.Selection, Text(firstPlacement, "deploymentId"), Text(replacement, "uid"), "log-a");
            await StartReadAsync(page, Text(firstPlacement, "deploymentId"), Text(replacement, "uid"), "log-a", true, journal, ct);
            await Assertions.Expect(page.GetByLabel("Runtime log output")).ToContainTextAsync(markerA);
            journal.Record(TestPhase.StreamReading, Text(firstPlacement, "deploymentId"), Text(replacement, "uid"), "log-a");
            relay.Pause();
            await Assertions.Expect(page.GetByTestId($"runtime-log-viewer-{Text(firstPlacement, "deploymentId")}")).ToContainTextAsync("could not be reached", new LocatorAssertionsToContainTextOptions { Timeout = 40_000 });
            var outage = await SendAsync(HttpMethod.Get, $"/api/v1/resource-deployments/{Text(firstPlacement, "deploymentId")}/diagnostics", null, ct);
            Assert.Equal("Unavailable", Text(outage.GetProperty("diagnostics"), "kind"));
            var independent = await SendAsync(HttpMethod.Get, $"/api/v1/resource-deployments/{Text(healthyPlacement, "deploymentId")}/diagnostics", null, ct);
            Assert.Equal("Available", Text(independent.GetProperty("diagnostics"), "kind"));
            await CaptureAsync(page, "outage");
            relay.Resume(); await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Refresh instances and events", Exact = true }).ClickAsync();
            await page.GetByLabel("Instance", new PageGetByLabelOptions { Exact = true }).SelectOptionAsync(Text(replacement, "uid"));
            await page.GetByLabel("Container", new PageGetByLabelOptions { Exact = true }).SelectOptionAsync("log-a");
            await StartReadAsync(page, Text(firstPlacement, "deploymentId"), Text(replacement, "uid"), "log-a", false, journal, ct);
            await Assertions.Expect(page.GetByLabel("Runtime log output")).ToContainTextAsync(markerA);
            await CaptureAsync(page, "reconnected");
            journal.Record(TestPhase.BodyCompleted);

            object StorageBody(bool enabled, long revision)
            {
                var body = JsonSerializer.SerializeToNode(new
                {
                    displayName = "Runtime logs test disk",
                    description = "Ephemeral diagnostics profile",
                    mediaType = "Network",
                    performanceTier = "Standard",
                    isEnabled = enabled,
                    isDefault = false,
                    targetMappings = enabled
                        ? new[]
                        {
                            new { deploymentTargetId = targetId, storageClassName = lab.StorageClassName },
                            new
                            {
                                deploymentTargetId = defaultTargetId,
                                storageClassName = fixture.LabState.GetRequiredTarget("default").StorageClassName
                            }
                        }
                        : new[]
                        {
                            new
                            {
                                deploymentTargetId = defaultTargetId,
                                storageClassName = fixture.LabState.GetRequiredTarget("default").StorageClassName
                            }
                        },
                    allowedResourceTypes = new[] { "pole/redis" },
                    tenantAccess = new
                    {
                        mode = "SelectedTenants",
                        tenantIds = new[] { fixture.Alpha.DefaultTenantId }
                    },
                    revision
                })!.AsObject();
                if (revision == 0)
                    body["technicalName"] = $"disk-{Guid.NewGuid():N}";

                return body;
            }
        }
        catch (Exception exception) { journal?.Record(TestPhase.Failed); primaryFailure = exception; throw; }
        finally
        {
            try
            {
                journal?.Record(TestPhase.Cleanup);
                await resources.DisposeAsync();
                if (primaryFailure is null) journal?.Record(TestPhase.Completed);
            }
            catch (Exception cleanupFailure)
            {
                journal?.Record(TestPhase.Failed);
                if (primaryFailure is null) throw;
                throw new AggregateException("Runtime diagnostics journey and cleanup failed.", primaryFailure, cleanupFailure);
            }
        }
    }

    private static async Task<IRequest> StartReadAsync(
        IPage page,
        string deploymentId,
        string uid,
        string container,
        bool follow,
        TestPhaseJournal journal,
        CancellationToken cancellationToken)
    {
        journal.Record(TestPhase.Action, deploymentId, uid, container);
        var observation = new TestActionObservation<IRequest>();
        void OnRequest(object? sender, IRequest candidate)
        {
            if (BrowserPhaseJournal.IsRuntimeRequest(candidate, deploymentId, uid, container, follow))
                observation.TryObserve(candidate);
        }
        page.Request += OnRequest;
        IRequest request;
        try
        {
            request = await observation.RunAsync(() => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = follow ? "Follow runtime logs" : "Read recent logs",
                Exact = true
            }).ClickAsync(), TimeSpan.FromSeconds(5), cancellationToken);
        }
        finally
        {
            page.Request -= OnRequest;
        }
        journal.Record(TestPhase.StreamOpening, deploymentId, uid, container);
        var response = await request.ResponseAsync().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.True(response is { Ok: true }, $"Runtime log opening returned HTTP {response?.Status ?? 0}.");
        return request;
    }

    private static object TargetMapping(string deploymentTargetId) => new
    {
        deploymentTargetId,
        nodeSelector = Array.Empty<object>(),
        requiredNodeAffinity = Array.Empty<object>(),
        preferredNodeAffinity = Array.Empty<object>(),
        tolerations = Array.Empty<object>(),
        runtimeClassName = (string?)null
    };
    private async Task<JsonElement> CreateRedisAsync(string name, string target, string storage, string compute, string group, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, "/api/v1/redis-services", new { name, resourceGroupId = group, computeProfileId = compute, spec = new { storage = new { sizeGb = 1, storageProfileId = storage } }, tags = new[] { "e2e" }, placement = new { deploymentTargetId = target } }, ct);
    private async Task<JsonElement> WaitForAppliedAsync(string id, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.readiness.WaitForAppliedAsync");
        while (true)
        {
            var value = await SendAsync(HttpMethod.Get, $"/api/v1/redis-services/{id}", null, ct);
            var placement = value.GetProperty("placement");
            if (Text(placement, "status") == "Applied") return placement.Clone();
            if (Text(placement, "status") == "Failed") throw new InvalidOperationException($"Runtime test apply failed: {Text(placement, "failureCode")}");
            await Task.Delay(500, ct);
        }
    }
    private async Task<JsonElement> WaitForInstancesAsync(string deployment, string? oldUid, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.readiness.WaitForInstancesAsync");
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct); ready.CancelAfter(TimeSpan.FromMinutes(3));
        while (true)
        {
            var value = await SendAsync(HttpMethod.Get, $"/api/v1/resource-deployments/{deployment}/diagnostics", null, ready.Token);
            var diagnostics = value.GetProperty("diagnostics");
            if (Text(diagnostics, "kind") == "Available" && diagnostics.GetProperty("instances").EnumerateArray().Any(item => item.GetProperty("ready").GetBoolean() && Text(item, "uid") != oldUid && item.GetProperty("containers").EnumerateArray().Any(container => Text(container, "name") == "log-a")))
            {
                TestPhaseJournal.Current?.Record(TestPhase.SnapshotAccepted, resourceId: deployment,
                    revision: value.GetProperty("deploymentRevision").GetInt64(),
                    generation: value.GetProperty("desiredGeneration").GetInt64(),
                    observationVersion: value.TryGetProperty("observationVersion", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt64(out var number) ? number : null);
                return value;
            }
            await Task.Delay(500, ready.Token);
        }
    }
    private string ResourceUrl(string id) => new Uri(fixture.FrontendBaseAddress, $"?view=redis&resource={id}&section=operations&tenant={fixture.Alpha.DefaultTenantId}").ToString();
    private async Task LoginAsync(IPage page)
    {
        await page.GotoAsync(fixture.FrontendBaseAddress.ToString());
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in", Exact = true }).ClickAsync();
        await page.GetByLabel("Username or email").FillAsync(fixture.Alpha.Email);
        await page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Password", Exact = true }).FillAsync(fixture.Alpha.Password);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true }).ClickAsync();
        await page.GetByLabel("Tenant").SelectOptionAsync(fixture.Alpha.DefaultTenantId);
    }
    private static async Task SelectAsync(IPage page, string deploymentId, string uid, string container)
    {
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Runtime logs and events", Exact = true }).ClickAsync();
        try { await page.GetByLabel("Instance", new PageGetByLabelOptions { Exact = true }).SelectOptionAsync(uid); }
        catch (TimeoutException exception)
        {
            await CaptureAsync(page, "discovery-failure");
            throw new InvalidOperationException($"Runtime viewer discovery: {await page.GetByTestId($"runtime-log-viewer-{deploymentId}").InnerTextAsync()}", exception);
        }
        await page.GetByLabel("Container", new PageGetByLabelOptions { Exact = true }).SelectOptionAsync(container);
    }
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, bool admin = false)
    {
        using var request = admin ? fixture.CreatePlatformAdminRequest(method, path) : fixture.CreateAuthenticatedRequest(method, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode, $"Runtime diagnostics API {method} returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
    private async Task DeleteAsync(string path, CancellationToken ct, bool admin = false)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteAsync");
        while (true)
        {
            using var request = admin ? fixture.CreatePlatformAdminRequest(HttpMethod.Delete, path) : fixture.CreateAuthenticatedRequest(HttpMethod.Delete, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, ct);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) return;
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await Task.Delay(500, ct); // RuntimeCell durable reference release follows native namespace deletion.
        }
    }
    private async Task DeleteRedisAsync(string id, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteRedisAsync");
        await DeleteAsync($"/api/v1/redis-services/{id}", ct);
        while (true)
        {
            using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/redis-services/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, ct);
            if (TestDeletion.IsComplete(response.StatusCode)) return;
            await Task.Delay(500, ct);
        }
    }
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static async Task CaptureAsync(IPage page, string stage)
    {
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "frontend-runtime-logs")); Directory.CreateDirectory(directory);
        foreach (var (viewport, width, height) in new[] { ("desktop", 1440, 900), ("mobile", 390, 844) })
        {
            await page.SetViewportSizeAsync(width, height);
            foreach (var theme in new[] { "light", "dark" })
            {
                if (await page.Locator("html").GetAttributeAsync("data-theme") != theme) await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Switch to {theme} theme", Exact = true }).ClickAsync();
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(directory, $"runtime-{viewport}-{stage}-{theme}.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"), "Runtime diagnostics overflow the viewport.");
            }
        }
        await page.SetViewportSizeAsync(1440, 900);
    }
}
