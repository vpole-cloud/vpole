using System.Collections.Immutable;
using Pole.Features.Events.Abstractions;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Xunit;

namespace Pole.Features.WebServices.Tests;

public sealed class WebServiceUpdateLogTests
{
    [Fact]
    public void Of_ReturnsEqual_ForEquivalentWebServiceSpecsWithDifferentCollectionInstances()
    {
        var previous = WebServiceTestData.CreateSpec().ToDomain();
        var next = WebServiceTestData.CreateSpec().ToDomain();

        var updateLog = UpdateLog<WebServiceSpec>.Of(previous, next);

        Assert.True(updateLog.AreEqual);
    }

    [Fact]
    public void Of_ReturnsDifferent_WhenWebServiceEnvironmentVariableChanges()
    {
        var previous = WebServiceTestData.CreateSpec().ToDomain();
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
        var previous = WebServiceTestData.CreateSpec().ToDomain() with
        {
            Config = new WebServiceConfig()
            {
                EnvironmentVariables = ImmutableDictionary<string, string>.Empty,
                PortMappings = [new PortMapping(80, "8080", "TCP", "http"), new PortMapping(443, "8443", "TCP",
                    "https")],
                ConnectionEnvironmentReferences = ImmutableDictionary<string, ConnectionEnvironmentReference>.Empty,
                SecretEnvironmentReferences = ImmutableDictionary<string, SecretEnvironmentReference>.Empty
            }
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
}
