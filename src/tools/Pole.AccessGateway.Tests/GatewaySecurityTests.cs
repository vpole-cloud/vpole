using System.Text;
using Pole.AccessGateway;
using Pole.Features.PrivateAccess.Primitives;
using Pole.Features.PrivateAccess.Runtime;
using Xunit;

namespace Pole.AccessGateway.Tests;

public sealed class GatewaySecurityTests
{
    [Theory]
    [InlineData("http://broker.example")]
    [InlineData("https://user:pass@broker.example")]
    [InlineData("https://broker.example/path")]
    [InlineData("https://broker.example?token=secret")]
    public void BrokerOriginRejectsNonOriginOrInsecureAddress(string address)
    {
        Assert.Throws<ArgumentException>(() => BrokerOrigin.Parse(address));
    }

    [Fact]
    public void BrokerOriginDerivesFixedSecureGatewayPaths()
    {
        var origin = BrokerOrigin.Parse("https://broker.example:9444");
        Assert.Equal("wss://broker.example:9444/private-access/gateways",
            origin.WebSocketPath("/private-access/gateways").AbsoluteUri);
        Assert.Equal("wss://broker.example:9444/private-access/gateway-streams",
            origin.WebSocketPath("/private-access/gateway-streams").AbsoluteUri);
    }

    [Theory]
    [InlineData("database.runtime-a.svc\n5432", true)]
    [InlineData("other.example\n5432", true)]
    [InlineData("database.runtime-a.svc\n0", false)]
    [InlineData("database.runtime-a.svc\n65536", false)]
    [InlineData("database.runtime-a.svc\n5432\n80", false)]
    [InlineData("database.runtime-a.svc\n-1", false)]
    [InlineData("database.runtime-a.svc\nfoo", false)]
    [InlineData("../database\n5432", false)]
    public void GatewayAcceptsOnlyBoundedDestinationMessage(string message, bool valid)
    {
        var frame = new PrivateAccessFrame(PrivateAccessFrameKind.Data,
            Encoding.ASCII.GetBytes(message));
        if (valid)
            Assert.Equal(5432, PrivateAccessGatewayProtocol.ReadDestination(frame).ServicePort);
        else
            Assert.Throws<InvalidDataException>(() =>
                PrivateAccessGatewayProtocol.ReadDestination(frame));
    }
}
