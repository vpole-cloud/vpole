namespace Pole.Features.DeploymentTargets.Domain;

public enum NetworkPolicyEnforcement
{
    Unknown,
    Supported,
    Unsupported
}

/// <summary>Platform operator attestation, not proof obtained from Kubernetes API discovery.</summary>
public sealed record DeploymentTargetNetworkPolicy(
    NetworkPolicyEnforcement Enforcement,
    string ClusterIdentity,
    long ConnectionVersion,
    DateTime? LastCheckedAtUtc,
    bool AllowIsolationNotEnforced)
{
    public static DeploymentTargetNetworkPolicy Unknown { get; } =
        new(NetworkPolicyEnforcement.Unknown, "", 0, null, false);

    public bool Matches(DeploymentTarget target) =>
        ClusterIdentity == target.ClusterIdentity &&
        ConnectionVersion == target.Connection.Version &&
        target.Health.FailureCode != "ClusterIdentityMismatch";

    public NetworkPolicyEnforcement EffectiveEnforcement(DeploymentTarget target) =>
        Matches(target) ? Enforcement : NetworkPolicyEnforcement.Unknown;

    public bool AllowsWorkloads(DeploymentTarget target) =>
        EffectiveEnforcement(target) == NetworkPolicyEnforcement.Supported ||
        Matches(target) && AllowIsolationNotEnforced;

    public string Readiness(DeploymentTarget target) =>
        EffectiveEnforcement(target) == NetworkPolicyEnforcement.Supported
            ? "EnforcementAttested"
            : Matches(target) && AllowIsolationNotEnforced
                ? "IsolationNotEnforced"
                : "EnforcementRequired";

    public string Reason(DeploymentTarget target) => EffectiveEnforcement(target) switch
    {
        NetworkPolicyEnforcement.Supported => "OperatorAttestedSupported",
        NetworkPolicyEnforcement.Unsupported => "OperatorAttestedUnsupported",
        _ => "EnforcementNotEstablished"
    };
}
