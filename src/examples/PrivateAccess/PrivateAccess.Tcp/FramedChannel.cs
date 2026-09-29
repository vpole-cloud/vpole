using System.Threading.Channels;

namespace Pole.PrivateAccess.Prototype.Tcp;

/// <summary>One bounded data queue and one reader per TLS connection.</summary>
internal sealed class FramedChannel : PrivateAccessChannel
{
    private readonly Wire wire;
    private readonly CancellationTokenSource lifetime;
    private readonly Channel<byte[]> incoming = Channel.CreateBounded<byte[]>(4);
    private readonly Task reader;
    private byte[] current = [];
    private int offset;
    private readonly Func<Frame, CancellationToken, Task> control;

    public FramedChannel(Wire wire, Func<Frame, CancellationToken, Task> control, CancellationToken ct)
    {
        this.wire = wire;
        this.control = control;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Disposing the TLS stream also interrupts a peer that is not reading.
        lifetime.Token.Register(() => wire.DisposeAsync().AsTask().GetAwaiter().GetResult());
        reader = ReceiveAsync();
    }

    private async Task ReceiveAsync()
    {
        var halfClosed = false;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var frame = await wire.ReceiveAsync(lifetime.Token);
                switch (frame.Kind)
                {
                    case FrameKind.Data when !halfClosed && frame.Payload.Length > 0:
                        await incoming.Writer.WriteAsync(frame.Payload, lifetime.Token);
                        break;
                    case FrameKind.HalfClose when !halfClosed && frame.Payload.Length == 0:
                        halfClosed = true;
                        incoming.Writer.TryComplete();
                        break;
                    default:
                        await control(frame, lifetime.Token);
                        break;
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException)
        {
            incoming.Writer.TryComplete(error);
        }
        finally
        {
            incoming.Writer.TryComplete();
            await lifetime.CancelAsync();
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.Length == 0)
            return 0;
        if (offset == current.Length)
        {
            if (!await incoming.Reader.WaitToReadAsync(cancellationToken))
                return 0;
            current = await incoming.Reader.ReadAsync(cancellationToken);
            offset = 0;
        }
        var count = Math.Min(buffer.Length, current.Length - offset);
        current.AsMemory(offset, count).CopyTo(buffer);
        offset += count;
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            var count = Math.Min(buffer.Length, Wire.MaximumPayload);
            await wire.SendAsync(FrameKind.Data, buffer[..count], cancellationToken);
            buffer = buffer[count..];
        }
    }

    public override ValueTask CompleteWritesAsync(CancellationToken cancellationToken) =>
        wire.SendAsync(FrameKind.HalfClose, ReadOnlyMemory<byte>.Empty, cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await reader;
        lifetime.Dispose();
    }
}
