// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Options;

namespace Pole.Features.ResourceDeployments.Application.Common;

public sealed class ResourceDeploymentSettings
{
    public const string SectionName = "ResourceDeployments";

    public int RecoveryBatchSize { get; init; } = 100;
    public int ClaimLeaseSeconds { get; init; } = 300;
    public int InitialRetryDelaySeconds { get; init; } = 30;
    public int MaximumRetryDelaySeconds { get; init; } = 900;
}

public sealed class ResourceDeploymentSettingsValidator : IValidateOptions<ResourceDeploymentSettings>
{
    public ValidateOptionsResult Validate(string? name, ResourceDeploymentSettings options)
    {
        var failures = new List<string>();
        if (options.RecoveryBatchSize is < 1 or > 1000)
            failures.Add("ResourceDeployments:RecoveryBatchSize must be between 1 and 1000.");
        if (options.ClaimLeaseSeconds is < 10 or > 3600)
            failures.Add("ResourceDeployments:ClaimLeaseSeconds must be between 10 and 3600.");
        if (options.InitialRetryDelaySeconds is < 1 or > 3600)
            failures.Add("ResourceDeployments:InitialRetryDelaySeconds must be between 1 and 3600.");
        if (options.MaximumRetryDelaySeconds < options.InitialRetryDelaySeconds ||
            options.MaximumRetryDelaySeconds > 86400)
            failures.Add(
                "ResourceDeployments:MaximumRetryDelaySeconds must be at least the initial delay and no more than 86400.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class ResourceDeploymentRetryDelayPolicy(
    ResourceDeploymentSettings settings
)
{
    public TimeSpan Calculate(int attemptCount)
    {
        if (attemptCount < 1)
            throw new ArgumentOutOfRangeException(nameof(attemptCount));

        var exponent = Math.Clamp(attemptCount - 1, 0, 30);
        var multiplier = 1L << exponent;
        var seconds = Math.Min(
            checked(settings.InitialRetryDelaySeconds * multiplier),
            settings.MaximumRetryDelaySeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}
