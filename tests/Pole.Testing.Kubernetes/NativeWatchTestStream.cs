using System.Text;
using System.Threading.Channels;

namespace Pole.Testing.Kubernetes;

public sealed class NativeWatchTestStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private byte[]? _current;
    private int _position;

    public int FramesWritten { get; private set; }

    public bool Disposed { get; private set; }

    private readonly TaskCompletionSource _nextReadAfterFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task NextReadAfterFrame => _nextReadAfterFrame.Task;

    public void Write(string frame)
    {
        FramesWritten++;
        _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(frame));
    }

    public void Complete() => _chunks.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current is null || _position == _current.Length)
        {
            if (_current is not null)
                _nextReadAfterFrame.TrySetResult();
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                return 0;
            if (!_chunks.Reader.TryRead(out _current))
                continue;
            _position = 0;
        }

        var length = Math.Min(buffer.Length, _current.Length - _position);
        _current.AsMemory(_position, length).CopyTo(buffer);
        _position += length;
        return length;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        Complete();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
