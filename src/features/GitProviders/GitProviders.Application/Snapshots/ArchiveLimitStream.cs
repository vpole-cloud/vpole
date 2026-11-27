// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.GitProviders.Application.Snapshots;

// Counts actual bytes, including TAR padding and metadata. Leaves the owned stream open.
internal sealed class ArchiveLimitStream(
    Stream inner,
    long limit
) : Stream
{
    public long Transferred { get; private set; }
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => Transferred;
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

    public override int Read(
        byte[] buffer,
        int offset,
        int count)
    {
        var read = inner.Read(buffer, offset, count);
        Account(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await inner.ReadAsync(buffer, ct);
        Account(read);
        return read;
    }

    public override void Write(
        byte[] buffer,
        int offset,
        int count)
    {
        Account(count);
        inner.Write(buffer, offset, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        Account(buffer.Length);
        await inner.WriteAsync(buffer, ct);
    }

    private void Account(int count)
    {
        if (count > limit - Transferred)
            throw new ArchiveSizeLimitException();

        Transferred += count;
    }
}

internal sealed class ArchiveSizeLimitException : Exception;
