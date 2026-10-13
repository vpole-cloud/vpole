using System.Net;
using System.Net.Sockets;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class RealtimePortReservationTests
{
    [Fact]
    public void Reserved_listener_port_is_unavailable_to_both_localhost_address_families()
    {
        var reservation = PoleE2EFixture.ReserveTcpPort();
        try
        {
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            foreach (var address in Socket.OSSupportsIPv6
                         ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }
                         : new[] { IPAddress.Loopback })
            {
                var contender = new TcpListener(address, port);
                try { Assert.Throws<SocketException>(() => contender.Start()); }
                finally { contender.Stop(); }
            }
        }
        finally { reservation.Stop(); }
    }
}
