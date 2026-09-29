using Pole.PrivateAccess.Prototype.Tcp;
using Xunit;

namespace PrivateAccess.Tests;

public sealed class LeaseTests
{
    [Fact]
    public void Replayed_reply_cannot_extend_a_lease()
    {
        using var guard = new LeaseGuard(TimeSpan.FromSeconds(1));
        var challenge = guard.Challenge();
        var reply = new LeaseReply(challenge.Nonce, 900);
        guard.Accept(reply);
        Assert.Throws<InvalidDataException>(() => guard.Accept(reply));
    }

    [Fact]
    public async Task Delayed_reply_is_measured_from_send_time()
    {
        using var guard = new LeaseGuard(TimeSpan.FromSeconds(1));
        var challenge = guard.Challenge();
        await Task.Delay(100);
        Assert.Throws<InvalidDataException>(() => guard.Accept(new LeaseReply(challenge.Nonce, 50)));
    }

    [Fact]
    public async Task Expired_lease_cannot_be_revived()
    {
        using var guard = new LeaseGuard(TimeSpan.FromMilliseconds(50));
        var challenge = guard.Challenge();
        await Task.Delay(100);
        Assert.Throws<InvalidDataException>(() => guard.Accept(new LeaseReply(challenge.Nonce, 50)));
        Assert.True(guard.Expired.IsCancellationRequested);
    }

    [Fact]
    public async Task Oversized_frame_is_rejected_before_allocation()
    {
        await using var wire = new Wire(new MemoryStream(new byte[] { 5, 127, 255, 255, 255 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => wire.ReceiveAsync(CancellationToken.None).AsTask());
    }
}
