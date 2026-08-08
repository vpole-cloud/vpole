using System.Collections.Immutable;
using Pole.Features.Events.Abstractions;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Domain;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Events;

public sealed class UpdateLogTests
{
    [Fact]
    public void Of_ReturnsEqual_ForEquivalentWebServiceSpecsWithDifferentCollectionInstances()
    {
        var previous = TestData.ValidWebServiceSpec().ToDomain();
        var next = TestData.ValidWebServiceSpec().ToDomain();

        var updateLog = UpdateLog<WebServiceSpec>.Of(previous, next);

        Assert.True(updateLog.AreEqual);
    }

    [Fact]
    public void Of_ReturnsDifferent_WhenWebServiceEnvironmentVariableChanges()
    {
        var previous = TestData.ValidWebServiceSpec().ToDomain();
        var next = previous with
        {
            Config = previous.Config with
            {
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Production"
                }.ToImmutableDictionary()
            }
        };

        var updateLog = UpdateLog<WebServiceSpec>.Of(previous, next);

        Assert.False(updateLog.AreEqual);
    }

    [Fact]
    public void Of_ReturnsDifferent_WhenWebServicePortMappingOrderChanges()
    {
        var previous = TestData.ValidWebServiceSpec().ToDomain() with
        {
            Config = new WebServiceConfig(
                EnvironmentVariables: ImmutableDictionary<string, string>.Empty,
                PortMappings:
                [
                    new PortMapping(80, "8080", "TCP", "http"),
                    new PortMapping(443, "8443", "TCP", "https")
                ])
        };
        var next = previous with
        {
            Config = previous.Config with
            {
                PortMappings =
                [
                    previous.Config.PortMappings[1],
                    previous.Config.PortMappings[0]
                ]
            }
        };

        var updateLog = UpdateLog<WebServiceSpec>.Of(previous, next);

        Assert.False(updateLog.AreEqual);
    }

    [Fact]
    public void Of_ReturnsEqual_ForEquivalentSetsWithDifferentConstructionOrder()
    {
        var previous = ImmutableHashSet.Create("cache", "prod");
        var next = ImmutableHashSet.Create("prod", "cache");

        var updateLog = UpdateLog<ImmutableHashSet<string>>.Of(previous, next);

        Assert.True(updateLog.AreEqual);
    }

    [Fact]
    public void Of_ReturnsEqual_ForEquivalentRedisSpecs()
    {
        var previous = TestData.ValidRedisSpec().ToDomain();
        var next = new RedisSpec(
            Storage: new Storage(5, TestData.DefaultStorageProfileId));

        var updateLog = UpdateLog<RedisSpec>.Of(previous, next);

        Assert.True(updateLog.AreEqual);
    }
}
