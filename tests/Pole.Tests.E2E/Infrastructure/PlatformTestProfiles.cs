using System.Text.Json;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

internal static class PlatformTestProfiles
{
    public static async Task DisableStorageAsync(
        PoleE2EFixture fixture,
        string id,
        CancellationToken ct)
    {
        using var read = fixture.CreatePlatformAdminRequest(HttpMethod.Get, "/api/v1/platform/storage/profiles");
        using var response = await fixture.Api.SendAsync(read, ct);
        Assert.True(response.IsSuccessStatusCode, $"Storage cleanup read returned HTTP {(int)response.StatusCode}.");
        var profiles = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var current = profiles.EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);
        using var update = fixture.CreatePlatformAdminRequest(
            HttpMethod.Put,
            $"/api/v1/platform/storage/profiles/{id}");
        update.Content = JsonContent.Create(
            new
            {
                displayName = current.GetProperty("displayName").GetString(),
                description = current.GetProperty("description").GetString(),
                mediaType = current.GetProperty("mediaType").GetString(),
                performanceTier = current.GetProperty("performanceTier").GetString(),
                isEnabled = false,
                isDefault = false,
                targetMappings = current.GetProperty("targetMappings"),
                allowedResourceTypes = current.GetProperty("allowedResourceTypes"),
                tenantAccess = current.GetProperty("tenantAccess"),
                sizeLimits = current.GetProperty("sizeLimits"),
                revision = current.GetProperty("revision").GetInt64(),
            });
        using var disabled = await fixture.Api.SendAsync(update, ct);
        Assert.True(disabled.IsSuccessStatusCode, $"Storage cleanup update returned HTTP {(int)disabled.StatusCode}.");
    }
}
