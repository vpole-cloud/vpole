// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Options;

namespace Pole.Features.Realtime.Application;

public sealed class RealtimeOptions
{
    public const string Section = "Realtime";

    public bool Enabled { get; set; }

    public string[] AllowedOrigins { get; set; } = [];

    public string[] EnabledTopics { get; set; } = [];

    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromMinutes(5);

    public string? KeyProtectionCertificatePath { get; set; }

    public string? KeyProtectionCertificatePassword { get; set; }
}

public sealed class RealtimeOptionsValidator : IValidateOptions<RealtimeOptions>
{
    public ValidateOptionsResult Validate(string? name, RealtimeOptions options)
    {
        if (!options.Enabled)
            return ValidateOptionsResult.Success;
        if (options.SessionLifetime <= TimeSpan.Zero || options.SessionLifetime > TimeSpan.FromMinutes(5))
            return ValidateOptionsResult.Fail(
                "Realtime:SessionLifetime must be positive and no longer than five minutes.");
        if (options.AllowedOrigins.Length == 0 || options.AllowedOrigins.Any(origin =>
                !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.UserInfo.Length != 0 || origin.EndsWith('/')))
            return ValidateOptionsResult.Fail(
                "Realtime:AllowedOrigins must contain exact HTTP(S) origins without trailing slashes.");
        return ValidateOptionsResult.Success;
    }
}
