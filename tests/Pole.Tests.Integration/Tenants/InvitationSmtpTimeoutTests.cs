// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pole.Features.Tenants.Application;
using Pole.Features.Tenants.Application.Invitations;
using Xunit;

namespace Pole.Tests.Integration.Tenants;

public sealed class InvitationSmtpTimeoutTests
{
    [Fact]
    public async Task StalledRelayTimesOutOneAttemptWithoutCancellingTheHost()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var services = Services(((IPEndPoint)listener.LocalEndpoint).Port, TimeSpan.FromSeconds(1));
        using var scope = services.CreateScope();
        using var host = new CancellationTokenSource();
        var accepted = listener.AcceptTcpClientAsync();
        var send = scope.ServiceProvider.GetRequiredService<SendInvitationMail>()(Mail(), host.Token);
        using var connection = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        // The relay accepts the TCP connection but deliberately never supplies an SMTP greeting.
        await Assert.ThrowsAsync<SmtpException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(host.IsCancellationRequested);
    }

    [Fact]
    public async Task HostCancellationRemainsCancellationRatherThanRetryableSmtpFailure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var services = Services(((IPEndPoint)listener.LocalEndpoint).Port, TimeSpan.FromSeconds(30));
        using var scope = services.CreateScope();
        using var host = new CancellationTokenSource();
        var accepted = listener.AcceptTcpClientAsync();
        var send = scope.ServiceProvider.GetRequiredService<SendInvitationMail>()(Mail(), host.Token);
        using var connection = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        await host.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SmtpBodyReportsTheExactUtcExpiry()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var services = Services(((IPEndPoint)listener.LocalEndpoint).Port, TimeSpan.FromSeconds(10));
        using var scope = services.CreateScope();
        var receive = ReceiveMailAsync(listener);
        await scope.ServiceProvider.GetRequiredService<SendInvitationMail>()(Mail(), CancellationToken.None);
        var body = await receive.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("expires at 2026-11-22 13:45:00 UTC", body);
        Assert.DoesNotContain("in seven days", body);
    }

    private static async Task<string> ReceiveMailAsync(TcpListener listener)
    {
        using var connection = await listener.AcceptTcpClientAsync();
        using var stream = connection.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
        await writer.WriteLineAsync("220 test relay");
        var message = new System.Text.StringBuilder();
        var inData = false;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (inData && line != ".")
            {
                message.AppendLine(line);
                continue;
            }

            if (line == ".")
            {
                await writer.WriteLineAsync("250 accepted");
                return message.ToString();
            }

            inData = line == "DATA";
            await writer.WriteLineAsync(inData ? "354 send message" : "250 OK");
        }

        throw new InvalidOperationException("SMTP connection closed before message delivery.");
    }

    private static ServiceProvider Services(int port, TimeSpan timeout)
    {
        var services = new ServiceCollection();
        services.AddTenantsFeature();
        services.AddSingleton<IOptions<InvitationSmtpOptions>>(
            Options.Create(
                new InvitationSmtpOptions
                {
                    Host = "127.0.0.1",
                    Port = port,
                    EnableSsl = false,
                    From = "pole@example.test",
                    SendTimeout = timeout,
                }));
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static InvitationMail Mail() => new(
        "recipient@example.test",
        "Tenant",
        "https://console.example.test/invitations",
        new DateTime(2026, 11, 22, 13, 45, 0, DateTimeKind.Utc));
}
