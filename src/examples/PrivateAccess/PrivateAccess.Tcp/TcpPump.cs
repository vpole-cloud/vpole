using System.Net.Sockets;

namespace Pole.PrivateAccess.Prototype.Tcp;

internal static class TcpPump
{
    public static async Task RunAsync(TcpClient socket, PrivateAccessChannel channel, CancellationToken ct)
    {
        var stream = socket.GetStream();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var registration = lifetime.Token.Register(socket.Dispose);
        var upload = UploadAsync();
        var download = DownloadAsync();
        await Task.WhenAll(upload, download);

        async Task UploadAsync()
        {
            try
            {
                var buffer = new byte[Wire.MaximumPayload];
                int count;
                while ((count = await stream.ReadAsync(buffer, lifetime.Token)) > 0)
                    await channel.WriteAsync(buffer.AsMemory(0, count), lifetime.Token);
                await channel.CompleteWritesAsync(lifetime.Token);
            }
            catch
            {
                await lifetime.CancelAsync();
                throw;
            }
        }

        async Task DownloadAsync()
        {
            try
            {
                var buffer = new byte[Wire.MaximumPayload];
                int count;
                while ((count = await channel.ReadAsync(buffer, lifetime.Token)) > 0)
                    await stream.WriteAsync(buffer.AsMemory(0, count), lifetime.Token);
                socket.Client.Shutdown(SocketShutdown.Send);
            }
            catch
            {
                await lifetime.CancelAsync();
                throw;
            }
        }
    }
}
