using Microsoft.Extensions.Options;

namespace Pole.Features.DeploymentTargets.Application.Common;

public enum DeploymentTargetBootstrapMode
{
    Auto,
    InCluster,
    Kubeconfig
}

public sealed class DeploymentTargetOptions
{
    public const string SectionName = "DeploymentTargets";

    public int ValidationTimeoutSeconds { get; init; } = 15;
    public DeploymentTargetBootstrapOptions Bootstrap { get; init; } = new();
}

public sealed class DeploymentTargetBootstrapOptions
{
    public bool Enabled { get; init; } = true;
    public string DisplayName { get; init; } = "Local cluster";
    public DeploymentTargetBootstrapMode Mode { get; init; } = DeploymentTargetBootstrapMode.Auto;
    public string? KubeconfigPath { get; init; }
    public string? KubeconfigContext { get; init; }
}

public sealed class DeploymentTargetOptionsValidator : IValidateOptions<DeploymentTargetOptions>
{
    public ValidateOptionsResult Validate(string? name, DeploymentTargetOptions options)
    {
        var failures = new List<string>();
        if (options.ValidationTimeoutSeconds is < 1 or > 120)
            failures.Add("DeploymentTargets:ValidationTimeoutSeconds must be between 1 and 120.");

        if (!Enum.IsDefined(options.Bootstrap.Mode))
            failures.Add("DeploymentTargets:Bootstrap:Mode is not supported.");

        if (string.IsNullOrWhiteSpace(options.Bootstrap.DisplayName) ||
            options.Bootstrap.DisplayName.Trim().Length > 80)
        {
            failures.Add("DeploymentTargets:Bootstrap:DisplayName is required and cannot exceed 80 characters.");
        }

        if (!string.IsNullOrWhiteSpace(options.Bootstrap.KubeconfigPath) &&
            !Path.IsPathFullyQualified(options.Bootstrap.KubeconfigPath))
        {
            failures.Add("DeploymentTargets:Bootstrap:KubeconfigPath must be an absolute path.");
        }

        if (options.Bootstrap.Mode == DeploymentTargetBootstrapMode.InCluster &&
            (!string.IsNullOrWhiteSpace(options.Bootstrap.KubeconfigPath) ||
             !string.IsNullOrWhiteSpace(options.Bootstrap.KubeconfigContext)))
        {
            failures.Add("InCluster bootstrap cannot specify kubeconfig path or context.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
