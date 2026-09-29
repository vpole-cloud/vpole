using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pole.PrivateAccess.Prototype.Tcp;

internal enum FrameKind : byte
{
    Client = 1,
    Connector,
    Open,
    Ready,
    Data,
    HalfClose,
    Challenge,
    Lease
}

internal sealed record Frame(
    FrameKind Kind,
    byte[] Payload);

internal sealed record ConnectorHello(string TargetId);
internal sealed record LeaseChallenge(Guid Nonce);
internal sealed record LeaseReply(
    Guid Nonce,
    int Milliseconds);

/// <summary>Bounded framing over authenticated TLS; never logs payloads.</summary>
internal sealed class Wire(Stream stream) : IAsyncDisposable
{
    public const int MaximumPayload = 16 * 1024;
    private readonly SemaphoreSlim writer = new(1, 1);
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    public async ValueTask SendAsync(FrameKind kind, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaximumPayload)
            throw new InvalidDataException("Frame exceeds protocol limit.");
        await writer.WaitAsync(ct);
        try
        {
            var header = new byte[5];
            header[0] = (byte)kind;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(payload, ct);
        }
        finally
        {
            writer.Release();
        }
    }

    public ValueTask SendJsonAsync<T>(FrameKind kind, T value, CancellationToken ct) =>
        SendAsync(kind, JsonSerializer.SerializeToUtf8Bytes(value, Json), ct);

    public static T Decode<T>(Frame frame) =>
        JsonSerializer.Deserialize<T>(frame.Payload, Json) ?? throw new InvalidDataException("Missing message.");

    public async ValueTask<Frame> ReceiveAsync(CancellationToken ct)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (length < 0 || length > MaximumPayload || !Enum.IsDefined((FrameKind)header[0]))
            throw new InvalidDataException("Invalid frame header.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return new Frame((FrameKind)header[0], payload);
    }

    public async ValueTask DisposeAsync() => await stream.DisposeAsync();
}
