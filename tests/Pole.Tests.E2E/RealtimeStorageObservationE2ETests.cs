using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Pole.Features.Realtime.Contracts;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class RealtimeStorageObservationE2ETests(PoleE2EFixture fixture)
{
    private readonly TestTimings _timings = new(typeof(RealtimeStorageObservationE2ETests));

    [Fact]
    public async Task Admin_native_storage_changes_publish_sanitized_coherent_snapshots_and_deny_regular_users()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var testEvidence = new TestPhaseJournal(nameof(Admin_native_storage_changes_publish_sanitized_coherent_snapshots_and_deny_regular_users)).Activate();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = budget.Token;
        var resources = new TestResourceScope("native storage observation E2E", timings: _timings);
        await resources.RunAsync(async () =>
        {
            var targets = await ReadAdminAsync("/api/v1/platform/deployment-targets", ct);
            var target = targets.EnumerateArray().Single(item => item.GetProperty("isDefault").GetBoolean());
            var id = target.GetProperty("id").GetString()!;
            var connectionVersion = target.GetProperty("connection").GetProperty("version").GetInt64();
            var endpoint = $"/api/v1/platform/storage/observation?deploymentTargetId={id}";
            var interest = new RealtimeSubscriptionRequest("storage-observations", ResourceId: id);

            using (var denied = fixture.CreateAuthenticatedRequest(HttpMethod.Get, endpoint, fixture.Beta))
            using (var response = await fixture.Api.SendAsync(denied, ct))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await using (var viewer = await ConnectAsync(false, ct))
                await Assert.ThrowsAsync<HubException>(() =>
                    viewer.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Subscribe", [new[] { interest }], ct));

            await using var admin = await ConnectAsync(true, ct);
            var changes = new ConcurrentQueue<RealtimeChange>();
            admin.On<RealtimeChange>("Changed",
                change =>
                {
                    if (change.Topic == "storage-observations" && change.ResourceId == id)
                        changes.Enqueue(change);
                });
            var acknowledgement =
                Assert.Single(
                    await admin.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Subscribe",
                        [new[] { interest }],
                        ct));
            await WaitUntilAsync(async phase =>
                {
                    var current =
                        Assert.Single(
                            await admin.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Heartbeat", [], phase));
                    Assert.Equal(acknowledgement.SubscriptionId, current.SubscriptionId);
                    return current.Covered;
                },
                "A complete native storage watch snapshot was not acknowledged.",
                ct);
            var before = await ReadAdminAsync(endpoint, ct);
            AssertSnapshot(before, id, connectionVersion);
            var beforeVersion = before.GetProperty("observationVersion").GetInt64();
            Assert.True(beforeVersion > 0);
            var canary = $"sensitive-test-{Guid.NewGuid():N}";
            var name = await fixture.CreateOwnedStorageObservationClassAsync(resources, id, canary, ct);

            JsonElement snapshot = default;
            await WaitUntilAsync(async phase =>
                {
                    if (!changes.Any(change => change.ObservationVersion > beforeVersion))
                        return false;
                    snapshot = await ReadAdminAsync(endpoint, phase);
                    AssertSnapshot(snapshot, id, connectionVersion);
                    var version = snapshot.GetProperty("observationVersion").GetInt64();
                    return version > beforeVersion && changes.Any(change => change.ObservationVersion >= version) &&
                           snapshot.GetProperty("storageClasses").EnumerateArray()
                               .Any(item => item.GetProperty("name").GetString() == name);
                },
                "A native StorageClass change did not reach its published shared observation.",
                ct);
            var observedClass = snapshot.GetProperty("storageClasses").EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == name);
            Assert.Equal(317, observedClass.GetProperty("provisioner").GetString()!.Length);
            Assert.False(snapshot.GetRawText().Contains(canary, StringComparison.Ordinal),
                "Sensitive native input leaked into a storage snapshot.");
            Assert.All(changes,
                change =>
                {
                    Assert.Null(change.TenantId);
                    Assert.Null(change.ResourceGroupId);
                    Assert.Equal(connectionVersion, change.Revision);
                    Assert.True(change.ObservationVersion > 0);
                });
            // Compatibility endpoints must obey the same payload policy; ordinary inventory
            // caching must not expose native parameter or unrestricted annotation dictionaries.
            var legacy = await ReadAdminAsync($"/api/v1/platform/storage/storage-classes?deploymentTargetId={id}", ct);
            Assert.Equal(317,
                legacy.EnumerateArray().Single(item => item.GetProperty("name").GetString() == name)
                    .GetProperty("provisioner").GetString()!.Length);
            Assert.False(legacy.GetRawText().Contains(canary, StringComparison.Ordinal),
                "Sensitive native input leaked into compatible storage discovery.");
        });
    }

    private static void AssertSnapshot(JsonElement snapshot, string targetId, long connectionVersion)
    {
        Assert.Equal(targetId, snapshot.GetProperty("deploymentTargetId").GetString());
        Assert.Equal(connectionVersion, snapshot.GetProperty("connectionVersion").GetInt64());
        Assert.True(snapshot.GetProperty("complete").GetBoolean());
        Assert.True(snapshot.GetProperty("checkedAtUtc").TryGetDateTimeOffset(out _));
        foreach (var field in new[] { "storageClasses", "persistentVolumes", "persistentVolumeClaims" })
            Assert.Equal(JsonValueKind.Array, snapshot.GetProperty(field).ValueKind);
    }

    private async Task<HubConnection> ConnectAsync(bool admin, CancellationToken ct)
    {
        var origin = fixture.FrontendBaseAddress.GetLeftPart(UriPartial.Authority);
        var cookies = new CookieContainer();
        using (var request = admin
                   ? fixture.CreatePlatformAdminRequest(HttpMethod.Post, "/api/v1/realtime/session")
                   : fixture.CreateAuthenticatedRequest(HttpMethod.Post, "/api/v1/realtime/session", fixture.Beta))
        {
            request.Headers.Add("Origin", origin);
            using var response = await fixture.Api.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            foreach (var cookie in response.Headers.GetValues("Set-Cookie"))
                cookies.SetCookies(fixture.ApiBaseAddress, cookie);
        }

        var connection = new HubConnectionBuilder().WithUrl(new Uri(fixture.ApiBaseAddress, "/api/v1/realtime/hub"),
            options =>
            {
                options.Cookies = cookies;
                options.Headers["Origin"] = origin;
                options.Transports = HttpTransportType.WebSockets;
            }).Build();
        try
        {
            await connection.StartAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<JsonElement> ReadAdminAsync(string path, CancellationToken ct)
    {
        using var request = fixture.CreatePlatformAdminRequest(HttpMethod.Get, path);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode,
            $"Storage observation read returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static Task WaitUntilAsync(Func<CancellationToken, Task<bool>> ready, string message, CancellationToken ct) =>
        TestWait.UntilAsync(ready, TimeSpan.FromSeconds(30), message, ct, TimeSpan.FromMilliseconds(250));
}
