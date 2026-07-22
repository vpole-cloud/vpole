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

public sealed record DeploymentTargetCapabilities
{
    [BsonConstructor]
    public DeploymentTargetCapabilities(
        long connectionVersion,
        DateTime? observedAtUtc,
        string? kubernetesVersion,
        DeploymentTargetNamespaceCapabilities namespaceCapabilities)
    {
        ConnectionVersion = DeploymentTargetValueGuard.Positive(
            connectionVersion,
            nameof(connectionVersion));
        NamespaceCapabilities = namespaceCapabilities ??
            throw new ArgumentNullException(nameof(namespaceCapabilities));

        if (observedAtUtc is null)
        {
            if (!string.IsNullOrWhiteSpace(kubernetesVersion) ||
                namespaceCapabilities != DeploymentTargetNamespaceCapabilities.Unknown)
            {
                throw new ArgumentException(
                    "Unknown deployment-target capabilities cannot contain observed values.",
                    nameof(observedAtUtc));
            }

            ObservedAtUtc = null;
            KubernetesVersion = null;
            return;
        }

        ObservedAtUtc = DeploymentTargetValueGuard.Utc(observedAtUtc.Value, nameof(observedAtUtc));
        KubernetesVersion = DeploymentTargetValueGuard.Required(
            kubernetesVersion,
            nameof(kubernetesVersion),
            DeploymentTargetValueGuard.MaximumKubernetesVersionLength);
    }

    public long ConnectionVersion { get; }
    public DateTime? ObservedAtUtc { get; }
    public string? KubernetesVersion { get; }
    public DeploymentTargetNamespaceCapabilities NamespaceCapabilities { get; }

    [BsonIgnore]
    public bool IsKnown => ObservedAtUtc is not null;

    public static DeploymentTargetCapabilities Unknown(long connectionVersion) =>
        new(
            connectionVersion,
            observedAtUtc: null,
            kubernetesVersion: null,
            DeploymentTargetNamespaceCapabilities.Unknown);

    public static DeploymentTargetCapabilities Observed(
        long connectionVersion,
        DateTime observedAtUtc,
        string kubernetesVersion,
        DeploymentTargetNamespaceCapabilities namespaceCapabilities) =>
        new(connectionVersion, observedAtUtc, kubernetesVersion, namespaceCapabilities);
}

public sealed record DeploymentTargetHealth
{
    [BsonConstructor]
    public DeploymentTargetHealth(
        long connectionVersion,
        DeploymentTargetHealthStatus status,
        DateTime? lastCheckedAtUtc,
        DateTime? lastHealthyAtUtc,
        string? failureCode,
        string? failureMessage)
    {
        ConnectionVersion = DeploymentTargetValueGuard.Positive(
            connectionVersion,
            nameof(connectionVersion));
        Status = status;
        LastCheckedAtUtc = lastCheckedAtUtc is null
            ? null
            : DeploymentTargetValueGuard.Utc(lastCheckedAtUtc.Value, nameof(lastCheckedAtUtc));
        LastHealthyAtUtc = lastHealthyAtUtc is null
            ? null
            : DeploymentTargetValueGuard.Utc(lastHealthyAtUtc.Value, nameof(lastHealthyAtUtc));

        if (LastHealthyAtUtc > LastCheckedAtUtc)
        {
            throw new ArgumentException(
                "The last healthy time cannot be later than the last validation time.",
                nameof(lastHealthyAtUtc));
        }

        switch (status)
        {
            case DeploymentTargetHealthStatus.Unknown:
                if (LastCheckedAtUtc is not null || LastHealthyAtUtc is not null ||
                    failureCode is not null || failureMessage is not null)
                {
                    throw new ArgumentException(
                        "Unknown deployment-target health cannot contain an observation.",
                        nameof(status));
                }
                break;
            case DeploymentTargetHealthStatus.Healthy:
                if (LastCheckedAtUtc is null || LastHealthyAtUtc is null ||
                    failureCode is not null || failureMessage is not null)
                {
                    throw new ArgumentException(
                        "Healthy deployment-target health requires successful observation times and no failure.",
                        nameof(status));
                }
                break;
            case DeploymentTargetHealthStatus.Degraded:
                if (LastCheckedAtUtc is null)
                {
                    throw new ArgumentException(
                        "Degraded deployment-target health requires a validation time.",
                        nameof(lastCheckedAtUtc));
                }
                FailureCode = DeploymentTargetValueGuard.Required(
                    failureCode,
                    nameof(failureCode),
                    DeploymentTargetValueGuard.MaximumFailureCodeLength);
                FailureMessage = DeploymentTargetValueGuard.Required(
                    failureMessage,
                    nameof(failureMessage),
                    DeploymentTargetValueGuard.MaximumFailureMessageLength);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown health status.");
        }

        FailureCode = null;
        FailureMessage = null;
    }

    public long ConnectionVersion { get; }
    public DeploymentTargetHealthStatus Status { get; }
    public DateTime? LastCheckedAtUtc { get; }
    public DateTime? LastHealthyAtUtc { get; }
    public string? FailureCode { get; }
    public string? FailureMessage { get; }

    public static DeploymentTargetHealth Unknown(long connectionVersion) =>
        new(connectionVersion, DeploymentTargetHealthStatus.Unknown, null, null, null, null);

    public static DeploymentTargetHealth Healthy(long connectionVersion, DateTime checkedAtUtc) =>
        new(
            connectionVersion,
            DeploymentTargetHealthStatus.Healthy,
            checkedAtUtc,
            checkedAtUtc,
            null,
            null);

    public static DeploymentTargetHealth Degraded(
        long connectionVersion,
        DateTime checkedAtUtc,
        DateTime? lastHealthyAtUtc,
        string failureCode,
        string safeFailureMessage) =>
        new(
            connectionVersion,
            DeploymentTargetHealthStatus.Degraded,
            checkedAtUtc,
            lastHealthyAtUtc,
            failureCode,
            safeFailureMessage);
}
