using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.DeploymentTargets.Domain;

public enum DeploymentTargetHealthStatus
{
    Unknown,
    Healthy,
    Degraded
}

public sealed record DeploymentTargetNamespaceCapabilities(
    bool CanGet,
    bool CanCreate,
    bool CanUpdate,
    bool CanPatch,
    bool CanDelete)
{
    public static DeploymentTargetNamespaceCapabilities Unknown { get; } =
        new(false, false, false, false, false);
}

public sealed record DeploymentTargetCapabilities(
    long ConnectionVersion,
    DateTime? ObservedAtUtc,
    string? KubernetesVersion,
    DeploymentTargetNamespaceCapabilities NamespaceCapabilities)
{
    [BsonIgnore]
    public bool IsKnown => ObservedAtUtc is not null;

    public static DeploymentTargetCapabilities Unknown(long connectionVersion) =>
        new(
            DeploymentTargetValueGuard.Positive(connectionVersion, nameof(connectionVersion)),
            ObservedAtUtc: null,
            KubernetesVersion: null,
            DeploymentTargetNamespaceCapabilities.Unknown);

    public static DeploymentTargetCapabilities Observed(
        long connectionVersion,
        DateTime observedAtUtc,
        string kubernetesVersion,
        DeploymentTargetNamespaceCapabilities namespaceCapabilities)
    {
        ArgumentNullException.ThrowIfNull(namespaceCapabilities);

        return new DeploymentTargetCapabilities(
            DeploymentTargetValueGuard.Positive(connectionVersion, nameof(connectionVersion)),
            DeploymentTargetValueGuard.Utc(observedAtUtc, nameof(observedAtUtc)),
            DeploymentTargetValueGuard.Required(
                kubernetesVersion,
                nameof(kubernetesVersion),
                DeploymentTargetValueGuard.MaximumKubernetesVersionLength),
            namespaceCapabilities);
    }
}

public sealed record DeploymentTargetHealth(
    long ConnectionVersion,
    DeploymentTargetHealthStatus Status,
    DateTime? LastCheckedAtUtc,
    DateTime? LastHealthyAtUtc,
    string? FailureCode,
    string? FailureMessage)
{
    public static DeploymentTargetHealth Unknown(long connectionVersion) =>
        new(
            DeploymentTargetValueGuard.Positive(connectionVersion, nameof(connectionVersion)),
            DeploymentTargetHealthStatus.Unknown,
            null,
            null,
            null,
            null);

    public static DeploymentTargetHealth Healthy(long connectionVersion, DateTime checkedAtUtc)
    {
        var normalizedCheckedAtUtc = DeploymentTargetValueGuard.Utc(
            checkedAtUtc,
            nameof(checkedAtUtc));

        return new DeploymentTargetHealth(
            DeploymentTargetValueGuard.Positive(connectionVersion, nameof(connectionVersion)),
            DeploymentTargetHealthStatus.Healthy,
            normalizedCheckedAtUtc,
            normalizedCheckedAtUtc,
            null,
            null);
    }

    public static DeploymentTargetHealth Degraded(
        long connectionVersion,
        DateTime checkedAtUtc,
        DateTime? lastHealthyAtUtc,
        string failureCode,
        string safeFailureMessage)
    {
        var normalizedCheckedAtUtc = DeploymentTargetValueGuard.Utc(
            checkedAtUtc,
            nameof(checkedAtUtc));
        DateTime? normalizedLastHealthyAtUtc = lastHealthyAtUtc is null
            ? null
            : DeploymentTargetValueGuard.Utc(lastHealthyAtUtc.Value, nameof(lastHealthyAtUtc));
        if (normalizedLastHealthyAtUtc > normalizedCheckedAtUtc)
        {
            throw new ArgumentException(
                "The last healthy time cannot be later than the last validation time.",
                nameof(lastHealthyAtUtc));
        }

        return new DeploymentTargetHealth(
            DeploymentTargetValueGuard.Positive(connectionVersion, nameof(connectionVersion)),
            DeploymentTargetHealthStatus.Degraded,
            normalizedCheckedAtUtc,
            normalizedLastHealthyAtUtc,
            DeploymentTargetValueGuard.Required(
                failureCode,
                nameof(failureCode),
                DeploymentTargetValueGuard.MaximumFailureCodeLength),
            DeploymentTargetValueGuard.Required(
                safeFailureMessage,
                nameof(safeFailureMessage),
                DeploymentTargetValueGuard.MaximumFailureMessageLength));
    }
}
