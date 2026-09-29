using System.Diagnostics;

namespace Pole.PrivateAccess.Prototype.Tcp;

/// <summary>
/// Reply deadlines are anchored to the local challenge send time, never receipt time.
/// A late or replayed reply cannot revive an expired session.
/// </summary>
internal sealed class LeaseGuard : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource expired = new();
    private readonly TimeSpan maximum;
    private Guid pending;
    private long sentAt;
    private long deadline;

    public LeaseGuard(TimeSpan maximum)
    {
        if (maximum <= TimeSpan.Zero || maximum > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(maximum));
        this.maximum = maximum;
        deadline = Stopwatch.GetTimestamp() + ToTicks(maximum);
        expired.CancelAfter(maximum);
    }

    public CancellationToken Expired => expired.Token;

    public LeaseChallenge Challenge()
    {
        lock (gate)
        {
            if (pending != Guid.Empty)
                throw new InvalidDataException("A renewal is already pending.");
            pending = Guid.NewGuid();
            sentAt = Stopwatch.GetTimestamp();
            return new LeaseChallenge(pending);
        }
    }

    public void Accept(LeaseReply reply)
    {
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (expired.IsCancellationRequested || now >= deadline || pending == Guid.Empty || reply.Nonce != pending)
                throw new InvalidDataException("Expired or unbound renewal.");
            if (reply.Milliseconds <= 0 || reply.Milliseconds > maximum.TotalMilliseconds)
                throw new InvalidDataException("Invalid lease duration.");
            var next = sentAt + ToTicks(TimeSpan.FromMilliseconds(reply.Milliseconds));
            if (next <= now)
                throw new InvalidDataException("Renewal arrived after its deadline.");
            pending = Guid.Empty;
            deadline = next;
            expired.CancelAfter(TimeSpan.FromSeconds((double)(next - now) / Stopwatch.Frequency));
        }
    }

    private static long ToTicks(TimeSpan duration) => (long)(duration.TotalSeconds * Stopwatch.Frequency);
    public void Dispose() => expired.Dispose();
}
