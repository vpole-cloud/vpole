using System.Net.WebSockets;

namespace Pole.Features.PrivateAccess.Primitives;

public enum PrivateAccessFrameKind : byte
{
    Data = 1,
    HalfClose = 2,
    Closed = 3,
    LeaseChallenge = 4,
    LeaseAccepted = 5,
    Ready = 6,
}

public sealed record PrivateAccessFrame(
    PrivateAccessFrameKind Kind,
    ReadOnlyMemory<byte> Payload
);

/// <summary>One bounded binary WebSocket message per frame. Callers serialize sends.</summary>
public static class PrivateAccessFrames
{
    public const int MaximumPayloadLength = 16 * 1024;
    public const int ChallengeLength = 32;

    public static async ValueTask SendAsync(
        WebSocket socket,
        PrivateAccessFrameKind kind,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        Validate(kind, payload.Length);
        var message = new byte[payload.Length + 1];
        message[0] = (byte)kind;
        payload.CopyTo(message.AsMemory(1));
        await socket.SendAsync(message.AsMemory(), WebSocketMessageType.Binary, true, cancellationToken);
    }

    public static async ValueTask<PrivateAccessFrame> ReceiveAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var message = new byte[MaximumPayloadLength + 1];
        var length = 0;
        while (true)
        {
            // Reject before reading another fragment into an empty buffer.
            if (length == message.Length)
                throw new InvalidDataException("Private Access frame exceeds its limit.");
            var received = await socket.ReceiveAsync(message.AsMemory(length), cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
                throw new EndOfStreamException("Private Access transport closed.");
            if (received.MessageType != WebSocketMessageType.Binary)
                throw new InvalidDataException("Private Access requires binary frames.");
            length += received.Count;
            if (!received.EndOfMessage)
                continue;
            if (length == 0)
                throw new InvalidDataException("Private Access frame is empty.");
            var kind = (PrivateAccessFrameKind)message[0];
            Validate(kind, length - 1);
            return new PrivateAccessFrame(kind, message.AsMemory(1, length - 1));
        }
    }

    private static void Validate(PrivateAccessFrameKind kind, int length)
    {
        var valid = kind switch
        {
            PrivateAccessFrameKind.Data => length is > 0 and <= MaximumPayloadLength,
            PrivateAccessFrameKind.HalfClose or PrivateAccessFrameKind.Closed
                or PrivateAccessFrameKind.Ready => length == 0,
            PrivateAccessFrameKind.LeaseChallenge or PrivateAccessFrameKind.LeaseAccepted => length == ChallengeLength,
            _ => false,
        };
        if (!valid)
            throw new InvalidDataException("Invalid Private Access frame shape.");
    }
}
