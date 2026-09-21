using System.Text.Json.Serialization;

namespace Pole.Features.DeploymentTargets.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<NetworkPolicyEnforcementStatus>))]
public enum NetworkPolicyEnforcementStatus
{
    Unknown,
    Supported,
    Unsupported
}

[JsonConverter(typeof(JsonStringEnumConverter<NetworkPolicyReadiness>))]
public enum NetworkPolicyReadiness
{
    EnforcementRequired,
    EnforcementAttested,
    IsolationNotEnforced
}

[JsonConverter(typeof(JsonStringEnumConverter<NetworkPolicyReason>))]
public enum NetworkPolicyReason
{
    EnforcementNotEstablished,
    OperatorAttestedSupported,
    OperatorAttestedUnsupported
}

[JsonConverter(typeof(JsonStringEnumConverter<NetworkPolicyEvidenceSource>))]
public enum NetworkPolicyEvidenceSource
{
    None,
    OperatorAttestation
}

public sealed record DeploymentTargetNetworkPolicyDto(
    NetworkPolicyEnforcementStatus Enforcement,
    NetworkPolicyReadiness Readiness,
    NetworkPolicyReason Reason,
    DateTime? LastCheckedAtUtc,
    NetworkPolicyEvidenceSource EvidenceSource,
    bool OverrideEnabled)
{
    public static DeploymentTargetNetworkPolicyDto Unknown { get; } = new(
        NetworkPolicyEnforcementStatus.Unknown,
        NetworkPolicyReadiness.EnforcementRequired,
        NetworkPolicyReason.EnforcementNotEstablished,
        null,
        NetworkPolicyEvidenceSource.None,
        false);

    [JsonIgnore]
    public bool AllowsWorkloads =>
        Readiness is NetworkPolicyReadiness.EnforcementAttested or NetworkPolicyReadiness.IsolationNotEnforced;
}
