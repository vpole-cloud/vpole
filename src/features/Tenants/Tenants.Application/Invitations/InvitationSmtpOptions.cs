using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Pole.Features.Tenants.Application.Invitations;

public sealed class InvitationSmtpOptions
{
    public const string SectionName = "Invitations:Smtp";

    public string Host { get; init; } = "";
    public int Port { get; init; } = 587;
    public bool EnableSsl { get; init; } = true;
    public string From { get; init; } = "";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

internal sealed class InvitationSmtpOptionsValidator : IValidateOptions<InvitationSmtpOptions>
{
    public ValidateOptionsResult Validate(string? name, InvitationSmtpOptions options)
    {
        const string section = InvitationSmtpOptions.SectionName;
        var failures = new List<string>();
        if (Uri.CheckHostName(options.Host) == UriHostNameType.Unknown)
            failures.Add($"{section}:Host must be a hostname or IP address.");
        if (options.Port is < 1 or > 65535)
            failures.Add($"{section}:Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(options.From) || !MailAddress.TryCreate(options.From, out _))
            failures.Add($"{section}:From must be a valid sender address.");
        if (options.SendTimeout <= TimeSpan.Zero || options.SendTimeout > TimeSpan.FromMinutes(5))
            failures.Add($"{section}:SendTimeout must be positive and at most five minutes.");
        if (string.IsNullOrWhiteSpace(options.Username) != string.IsNullOrEmpty(options.Password))
            failures.Add($"{section}:Username and {section}:Password must be configured together.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
