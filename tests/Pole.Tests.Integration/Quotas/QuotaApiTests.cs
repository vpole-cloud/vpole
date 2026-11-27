// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Text.Json;
using Pole.Features.Core.Multitenancy;
using Pole.Tests.Integration.Http;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Quotas;

[Collection(IntegrationTestCollection.Tenant4)]
public sealed class QuotaApiTests(
    MongoReplicaSetFixture fixture
) : ApiIntegrationTestBase(fixture)
{
    [Theory]
    [InlineData(null)]
    [InlineData("malformed-active-selection")]
    [InlineData("507f1f77bcf86cd799439099")]
    public async Task PlatformOperationUsesRouteTenantWithoutConsumingHeader(string? tenantHeader)
    {
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        if (tenantHeader is not null)
            Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, tenantHeader);
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");

        var response = await Client.GetAsync($"/api/v1/platform/tenants/{Data.TenantId}/quota");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(Data.TenantId, body.RootElement.GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task PolicyRequiresPlatformAdmin_AndTenantCanReadOwnUsage()
    {
        var path = $"/api/v1/platform/tenants/{Data.TenantId}/quota";
        var body = new
        {
            limits = new[]
            {
                new { key = "WebServiceCount", value = 1 },
            },
        };

        var forbidden = await Client.PutAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var updated = await Client.PutAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var usage = await Client.GetAsync("/api/v1/tenant-quotas/usage");
        Assert.Equal(HttpStatusCode.OK, usage.StatusCode);
    }

    [Fact]
    public async Task UsageRequiresTenantHeader()
    {
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);

        var response = await Client.GetAsync("/api/v1/tenant-quotas/usage");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PolicyRejectsUnknownQuotaKey()
    {
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var response = await Client.PutAsJsonAsync(
            $"/api/v1/platform/tenants/{Data.TenantId}/quota",
            new
            {
                limits = new[] { new { key = "UnknownQuota", value = 1 } },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("request.validation.failed", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("Body.Limits[0].Key", out _));
    }
}
