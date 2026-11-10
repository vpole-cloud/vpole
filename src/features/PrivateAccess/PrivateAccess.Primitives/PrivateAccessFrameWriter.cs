using System.Net.WebSockets;

namespace Pole.Features.PrivateAccess.Primitives;

/// <summary>Serializes payload and lease-control writes without buffering payload queues.</summary>
public sealed class PrivateAccessFrameWriter(
    WebSocket socket
) : IDisposable
{
    private readonly SemaphoreSlim sending = new(1, 1);

    public void Dispose() => sending.Dispose();

    public async ValueTask SendAsync(
        PrivateAccessFrameKind kind,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await sending.WaitAsync(cancellationToken);
        try
        {
            await PrivateAccessFrames.SendAsync(socket, kind, payload, cancellationToken);
        }
        finally
        {
            sending.Release();
        }
    }
}
