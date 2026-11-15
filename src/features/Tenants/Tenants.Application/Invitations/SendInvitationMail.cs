using System.Globalization;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Pole.Features.Tenants.Application.Invitations;

public sealed record InvitationMail(
    string Recipient,
    string TenantName,
    string AcceptanceUrl,
    DateTime ExpiresAtUtc
)
{
    public override string ToString() => "InvitationMail(redacted)";
}

public delegate Task SendInvitationMail(InvitationMail mail, CancellationToken ct);

internal sealed class SmtpInvitationMail(
    IOptions<InvitationSmtpOptions> options
)
{
    public async Task SendAsync(InvitationMail mail, CancellationToken ct)
    {
        var settings = options.Value;
        using var message = new MailMessage(
            settings.From,
            mail.Recipient,
            "You are invited to a Pole tenant",
            $"You have been invited to join {mail.TenantName}.\n\n" +
            $"Review the invitation and choose whether to accept:\n{mail.AcceptanceUrl}\n\n" +
            $"This invitation expires at {mail.ExpiresAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC. " +
            "Opening the link does not grant access.");
        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.EnableSsl,
            UseDefaultCredentials = false,
            Credentials = string.IsNullOrWhiteSpace(settings.Username)
                ? null
                : new NetworkCredential(settings.Username, settings.Password),
        };
        using var deadline = new CancellationTokenSource(settings.SendTimeout);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            await client.SendMailAsync(message, attempt.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            // Local expiry is retryable; host cancellation keeps its original cancellation semantics.
            throw new SmtpException("Invitation SMTP delivery timed out.");
        }
    }
}
