using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using Pole.Features.PrivateAccess.Primitives;

namespace Pole.Features.PrivateAccess.Runtime;

/// <summary>
///     Control messages are Data frames on the gateway control socket; the first
///     Data frame on a gateway stream carries the server-owned destination.
/// </summary>
public static class PrivateAccessGatewayProtocol
{
    public static async ValueTask SendOpenStreamAsync(
        PrivateAccessFrameWriter writer,
        string ticket,
        CancellationToken cancellationToken)
    {
        if (!ValidTicket(ticket))
            throw new ArgumentException("Invalid gateway stream ticket.", nameof(ticket));
        await writer.SendAsync(PrivateAccessFrameKind.Data, Encoding.ASCII.GetBytes(ticket), cancellationToken);
    }

    public static string ReadOpenStream(PrivateAccessFrame frame)
    {
        if (frame.Kind != PrivateAccessFrameKind.Data || frame.Payload.Length != 64)
            throw new InvalidDataException("Invalid gateway control message.");
        var ticket = Encoding.ASCII.GetString(frame.Payload.Span);
        return ValidTicket(ticket) ? ticket : throw new InvalidDataException("Invalid gateway stream ticket.");
    }

    public static async ValueTask SendDestinationAsync(
        WebSocket socket,
        PrivateAccessGatewayDestination destination,
        CancellationToken cancellationToken)
    {
        ValidateDestination(destination);
        await PrivateAccessFrames.SendAsync(
            socket,
            PrivateAccessFrameKind.Data,
            Encoding.ASCII.GetBytes(
                $"{destination.ServiceDns}\n{destination.ServicePort.ToString(CultureInfo.InvariantCulture)}"),
            cancellationToken);
    }

    public static PrivateAccessGatewayDestination ReadDestination(PrivateAccessFrame frame)
    {
        if (frame.Kind != PrivateAccessFrameKind.Data || frame.Payload.Length is < 3 or > 260)
            throw new InvalidDataException("Invalid gateway destination message.");
        var value = Encoding.ASCII.GetString(frame.Payload.Span);
        var separator = value.IndexOf('\n');
        if (separator <= 0 || separator == value.Length - 1 || separator != value.LastIndexOf('\n') || !int.TryParse(
                value.AsSpan(separator + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var port))
            throw new InvalidDataException("Invalid gateway destination message.");
        var destination = new PrivateAccessGatewayDestination(value[..separator], port);
        ValidateDestination(destination);
        return destination;
    }

    private static bool ValidTicket(string ticket) => ticket.Length == 64 && ticket.All(Uri.IsHexDigit);

    private static void ValidateDestination(PrivateAccessGatewayDestination destination)
    {
        if (destination.ServicePort is < 1 or > 65535 || destination.ServiceDns.Length is < 1 or > 253 || destination
                .ServiceDns
                .Split('.')
                .Any(label => label.Length is < 1 or > 63 || !char.IsAsciiLetterOrDigit(label[0]) ||
                    !char.IsAsciiLetterOrDigit(label[^1]) || label.Any(character =>
                        !(char.IsAsciiLetterOrDigit(character) || character == '-'))))
            throw new InvalidDataException("Invalid gateway destination.");
    }
}

public sealed record PrivateAccessGatewayDestination(
    string ServiceDns,
    int ServicePort
);
