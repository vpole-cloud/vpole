using System.Globalization;
using System.Net.WebSockets;
using Pole.Features.PrivateAccess.Runtime;

namespace Pole.Connector;

public static class WorkerSession
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var origin = BrokerOrigin.Parse(RequiredEnvironment("POLE_ACCESS_BROKER"));
        _ = RequiredEnvironment("POLE_ACCESS_SESSION");
        var service = RequiredEnvironment("POLE_ACCESS_SERVICE");
        if (service.Length > 253 || service.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
            throw new ArgumentException("Worker service DNS is invalid.");
        if (!int.TryParse(RequiredEnvironment("POLE_ACCESS_PORT"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new ArgumentException("Worker service port is invalid.");

        var ticketPath = Environment.GetEnvironmentVariable("POLE_ACCESS_CREDENTIAL_FILE") ??
            "/run/pole-access/ticket";
        var ticketFile = new FileInfo(ticketPath);
        if (!ticketFile.Exists || ticketFile.Length is < 1 or > 256)
            throw new IOException("Worker credential file is unavailable.");
        var ticket = (await File.ReadAllTextAsync(ticketPath, cancellationToken)).Trim();
        if (ticket.Length is < 1 or > 256 || ticket.Any(char.IsWhiteSpace))
            throw new InvalidDataException("Worker credential has an invalid format.");

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {ticket}");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connect.CancelAfter(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(origin.WebSocketPath("/private-access/workers"),
            invoker, connect.Token);
        await new PrivateAccessWorker(TimeProvider.System).RunAsync(
            socket, service, port, cancellationToken);
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException("Required worker configuration is missing.");
}
