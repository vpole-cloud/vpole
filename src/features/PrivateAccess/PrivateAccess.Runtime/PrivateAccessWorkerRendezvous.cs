using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Pole.Features.PrivateAccess.Runtime;

/// <summary>Single broker-local rendezvous capabilities never appear in URLs or logs.</summary>
public sealed class PrivateAccessWorkerRendezvous(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, PendingPrivateAccessWorker> pending = new();

    public PendingPrivateAccessWorker Prepare()
    {
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var worker = new PendingPrivateAccessWorker(ticket, time.GetTimestamp());
        if (!pending.TryAdd(Digest(ticket), worker))
            throw new InvalidOperationException("Could not allocate a worker rendezvous.");
        return worker;
    }

    public PendingPrivateAccessWorker? Claim(string ticket)
    {
        if (ticket.Length != 64 || !ticket.All(Uri.IsHexDigit))
            return null;
        if (!pending.TryRemove(Digest(ticket), out var worker))
            return null;
        return time.GetElapsedTime(worker.CreatedTimestamp) < TimeSpan.FromSeconds(30) && !worker.Finished.IsCompleted
            ? worker
            : null;
    }

    public void Remove(PendingPrivateAccessWorker worker)
    {
        pending.TryRemove(Digest(worker.Ticket), out _);
        worker.Complete();
    }

    private static string Digest(string ticket) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(ticket)));
}

public sealed class PendingPrivateAccessWorker
{
    private readonly TaskCompletionSource<WebSocket> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PendingPrivateAccessWorker(string ticket, long createdTimestamp)
    {
        Ticket = ticket;
        CreatedTimestamp = createdTimestamp;
    }

    public string Ticket { get; }
    public long CreatedTimestamp { get; }
    public Task<WebSocket> Connected => connected.Task;
    public Task Finished => finished.Task;
    public bool Accept(WebSocket socket) => !Finished.IsCompleted && connected.TrySetResult(socket);
    public void Complete()
    {
        finished.TrySetResult();
        connected.TrySetCanceled();
    }

    public override string ToString() => "PendingPrivateAccessWorker(Ticket=[REDACTED])";
}
