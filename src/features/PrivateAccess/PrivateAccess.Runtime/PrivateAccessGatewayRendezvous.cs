using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Pole.Features.PrivateAccess.Runtime;

/// <summary>One-use broker-local tickets are bound to the active gateway registration.</summary>
public sealed class PrivateAccessGatewayRendezvous(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, PendingPrivateAccessGatewayStream> pending = new();

    public PendingPrivateAccessGatewayStream Prepare(
        PrivateAccessGatewayRegistration registration,
        PrivateAccessGatewayDestination destination)
    {
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var stream = new PendingPrivateAccessGatewayStream(
            ticket, time.GetTimestamp(), registration, destination);
        if (!pending.TryAdd(Digest(ticket), stream))
            throw new InvalidOperationException("Could not allocate a gateway stream rendezvous.");
        return stream;
    }

    public PendingPrivateAccessGatewayStream? Claim(
        string ticket,
        PrivateAccessGatewayRegistration registration)
    {
        if (ticket.Length != 64 || !ticket.All(Uri.IsHexDigit))
            return null;
        var digest = Digest(ticket);
        if (!pending.TryGetValue(digest, out var stream) ||
            !ReferenceEquals(stream.Registration, registration) ||
            !pending.TryRemove(new KeyValuePair<string, PendingPrivateAccessGatewayStream>(digest, stream)))
            return null;
        if (time.GetElapsedTime(stream.CreatedTimestamp) < TimeSpan.FromSeconds(30) &&
            !stream.Finished.IsCompleted)
            return stream;
        stream.Complete();
        return null;
    }

    public void Remove(PendingPrivateAccessGatewayStream stream)
    {
        pending.TryRemove(Digest(stream.Ticket), out _);
        stream.Complete();
    }

    private static string Digest(string ticket) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(ticket)));
}

public sealed class PendingPrivateAccessGatewayStream
{
    private readonly TaskCompletionSource<WebSocket> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PendingPrivateAccessGatewayStream(
        string ticket,
        long createdTimestamp,
        PrivateAccessGatewayRegistration registration,
        PrivateAccessGatewayDestination destination)
    {
        Ticket = ticket;
        CreatedTimestamp = createdTimestamp;
        Registration = registration;
        Destination = destination;
    }

    public string Ticket { get; }
    public long CreatedTimestamp { get; }
    public PrivateAccessGatewayRegistration Registration { get; }
    public PrivateAccessGatewayDestination Destination { get; }
    public Task<WebSocket> Connected => connected.Task;
    public Task Finished => finished.Task;
    public bool Accept(WebSocket socket) => !Finished.IsCompleted && connected.TrySetResult(socket);
    public void Complete()
    {
        finished.TrySetResult();
        connected.TrySetCanceled();
    }

    public override string ToString() => "PendingPrivateAccessGatewayStream(Ticket=[REDACTED])";
}
