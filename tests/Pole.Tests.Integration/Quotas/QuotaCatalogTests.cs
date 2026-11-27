// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Quotas.Application;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Contracts;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Common;
using Xunit;

namespace Pole.Tests.Integration.Quotas;

public sealed class QuotaCatalogTests
{
    [Fact]
    public void CatalogRejectsDuplicateQuotaKeys()
    {
        var key = new QuotaKey("duplicate");

        var exception = Assert.Throws<InvalidOperationException>(() => new QuotaCatalog(
            [new TestQuota(key), new TestQuota(key)],
            []));

        Assert.Contains("registered more than once", exception.Message);
    }

    [Fact]
    public void CatalogRejectsDuplicateProfilesForSpecType()
    {
        var key = new QuotaKey("test");

        var exception = Assert.Throws<InvalidOperationException>(() => new QuotaCatalog(
            [new TestQuota(key)],
            [new TestProfile(key), new TestProfile(key)]));

        Assert.Contains("More than one quota profile", exception.Message);
    }

    [Fact]
    public void CatalogRejectsProfileKeysWithoutDefinitions()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new QuotaCatalog(
            [],
            [new TestProfile(new QuotaKey("missing"))]));

        Assert.Contains("unregistered quotas", exception.Message);
    }

    [Fact]
    public void CatalogRejectsQuotaValuesNotDeclaredByProfile()
    {
        var declared = new QuotaKey("declared");
        var returned = new QuotaKey("returned");
        var catalog = new QuotaCatalog(
            [new TestQuota(declared), new TestQuota(returned)],
            [new TestProfile(declared, returned)]);

        var exception = Assert.Throws<InvalidOperationException>(() => catalog.Calculate(new TestSpec()));

        Assert.Contains("returned undeclared quotas", exception.Message);
    }


    [Fact]
    public void WorkloadFeatureRegistrationsContributeTheirOwnQuotaProfiles()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddFunctionAppsFeature();
        services.AddWebServicesFeature();
        services.AddQuotasFeature(configuration);
        using var provider = services.BuildServiceProvider();

        var profiles = provider.GetServices<IQuotaProfile>().ToArray();

        Assert.Contains(profiles, profile => profile is FunctionAppQuotaProfile);
        Assert.Contains(profiles, profile => profile is WebServiceQuotaProfile);
        _ = provider.GetRequiredService<QuotaCatalog>().Definitions;
    }

    private sealed record TestSpec;

    private sealed class TestQuota(
        QuotaKey key
    ) : Quota<TestSpec>
    {
        public override QuotaKey Key => key;
        public override string Unit => "tests";
        public override long Calculate(TestSpec input) => 1;
    }

    private sealed class TestProfile(
        QuotaKey key,
        QuotaKey? returnedKey = null
    ) : QuotaProfile<TestSpec>
    {
        public override IReadOnlyCollection<QuotaKey> Keys { get; } = [key];
        public override QuotaDelta Calculate(TestSpec spec) => QuotaDelta.From(returnedKey ?? key, 1);
    }
}
