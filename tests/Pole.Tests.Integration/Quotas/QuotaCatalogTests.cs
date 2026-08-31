using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Pole.Features.Quotas.Application;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Postgres.Application;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Common;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Quotas;

public sealed class QuotaCatalogTests
{
    [Fact]
    public void CatalogRejectsDuplicateQuotaKeys()
    {
        var key = new QuotaKey("duplicate");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new QuotaCatalog(
                [new TestQuota(key), new TestQuota(key)],
                []));

        Assert.Contains("registered more than once", exception.Message);
    }

    [Fact]
    public void CatalogRejectsDuplicateProfilesForSpecType()
    {
        var key = new QuotaKey("test");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new QuotaCatalog(
                [new TestQuota(key)],
                [new TestProfile(key), new TestProfile(key)]));

        Assert.Contains("More than one quota profile", exception.Message);
    }

    [Fact]
    public void CatalogRejectsProfileKeysWithoutDefinitions()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new QuotaCatalog(
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

        var exception = Assert.Throws<InvalidOperationException>(() =>
            catalog.Calculate(new TestSpec()));

        Assert.Contains("returned undeclared quotas", exception.Message);
    }

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
        Assert.False(catalog.Contains(WebServiceCountQuota.QuotaKey));
        Assert.IsType<RedisQuotaProfile>(catalog.GetProfile<RedisQuotaInput>());
    }

    [Fact]
    public void WorkloadFeatureRegistrationsContributeTheirOwnQuotaProfiles()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddFunctionAppsFeature();
        services.AddPostgresFeature(configuration);
        services.AddRedisServicesFeature();
        services.AddWebServicesFeature();
        services.AddQuotasFeature(configuration);
        using var provider = services.BuildServiceProvider();

        var profiles = provider.GetServices<IQuotaProfile>().ToArray();

        Assert.Contains(profiles, profile => profile is FunctionAppQuotaProfile);
        Assert.Contains(profiles, profile => profile is PostgresQuotaProfile);
        Assert.Contains(profiles, profile => profile is RedisQuotaProfile);
        Assert.Contains(profiles, profile => profile is WebServiceQuotaProfile);
        _ = provider.GetRequiredService<QuotaCatalog>().Definitions;
    }

    [Fact]
    public void RedisProfileCalculatesSharedAndFeatureQuota()
    {
        var profile = new RedisQuotaProfile(
            new CpuQuota(),
            new RamQuota(),
            new WorkloadInstancesQuota(),
            new RedisServiceCountQuota());
        var input = RedisQuotaInput.Create(1000, 128);

        var impact = profile.Calculate(input);

        Assert.Equal(1000, impact[CpuQuota.QuotaKey]);
        Assert.Equal(128, impact[RamQuota.QuotaKey]);
        Assert.Equal(1, impact[WorkloadInstancesQuota.QuotaKey]);
        Assert.Equal(1, impact[RedisServiceCountQuota.QuotaKey]);
    }

    [Fact]
    public void WebServiceProfileUsesAutoscalingMaximumAsWorkloadSize()
    {
        var profile = new WebServiceQuotaProfile(
            new CpuQuota(),
            new RamQuota(),
            new WorkloadInstancesQuota(),
            new WebServiceCountQuota());
        var dto = TestData.ValidWebServiceSpec() with
        {
            Autoscaling = new AutoscalingSpecDto(
                2,
                5,
                new AutoscalingMetricDto("cpu", 75))
        };
        var spec = dto.ToDomain();

        var impact = profile.Calculate(
            WebServiceQuotaInput.Create(250, 256, spec));

        Assert.Equal(1250, impact[CpuQuota.QuotaKey]);
        Assert.Equal(1280, impact[RamQuota.QuotaKey]);
        Assert.Equal(5, impact[WorkloadInstancesQuota.QuotaKey]);
        Assert.Equal(1, impact[WebServiceCountQuota.QuotaKey]);
    }

    [Fact]
    public void WebServiceProfileSumsDesiredCapacityAcrossDeploymentTargets()
    {
        var profile = new WebServiceQuotaProfile(
            new CpuQuota(),
            new RamQuota(),
            new WorkloadInstancesQuota(),
            new WebServiceCountQuota());
        var input = WebServiceQuotaInput.Create(
        [
            WebServiceDeploymentQuotaInput.Create(250, 256, 2),
            WebServiceDeploymentQuotaInput.Create(1000, 2048, 3)
        ]);

        var impact = profile.Calculate(input);

        Assert.Equal(3500, impact[CpuQuota.QuotaKey]);
        Assert.Equal(6656, impact[RamQuota.QuotaKey]);
        Assert.Equal(5, impact[WorkloadInstancesQuota.QuotaKey]);
        Assert.Equal(1, impact[WebServiceCountQuota.QuotaKey]);
    }

    private sealed record TestSpec;

    private sealed class TestQuota(QuotaKey key) : Quota<TestSpec>
    {
        public override QuotaKey Key => key;
        public override string Unit => "tests";
        public override long Calculate(TestSpec input) => 1;
    }

    private sealed class TestProfile(QuotaKey key, QuotaKey? returnedKey = null) : QuotaProfile<TestSpec>
    {
        public override IReadOnlyCollection<QuotaKey> Keys { get; } = [key];
        public override QuotaDelta Calculate(TestSpec spec) => QuotaDelta.From(returnedKey ?? key, 1);
    }
}
