// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Quotas.Application;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Contracts;
using Pole.Features.RedisServices.Application;
using Pole.Features.RedisServices.Application.Common;
using Xunit;

namespace Pole.Features.RedisServices.Tests;

public sealed class RedisQuotaRegistrationTests
{
    [Fact]
    public void FeatureRegistrationOwnsItsQuotaProfile()
    {
        var services = new ServiceCollection();
        services.AddRedisServicesFeature();
        services.AddQuotasFeature(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var catalog = provider.GetRequiredService<QuotaCatalog>();

        Assert.True(catalog.Contains(CpuQuota.QuotaKey));
        Assert.True(catalog.Contains(RedisServiceCountQuota.QuotaKey));
        Assert.IsType<RedisQuotaProfile>(catalog.GetProfile<RedisQuotaInput>());
    }
}
