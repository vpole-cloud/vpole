using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.DeploymentTargets.Domain;

public enum NetworkPolicyEnforcement
{
    Unknown,
    Supported,
    Unsupported
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(UnattestedNetworkPolicy), typeof(AttestedNetworkPolicy))]
public abstract record DeploymentTargetNetworkPolicy
{
    public static DeploymentTargetNetworkPolicy Unknown { get; } = new UnattestedNetworkPolicy();

    public bool Matches(DeploymentTarget target) =>
        this is AttestedNetworkPolicy evidence &&
        evidence.ClusterIdentity == target.ClusterIdentity &&
        evidence.ConnectionVersion == target.Connection.Version &&
        target.Health.FailureCode != "ClusterIdentityMismatch";

    public NetworkPolicyEnforcement EffectiveEnforcement(DeploymentTarget target) =>
        this is AttestedNetworkPolicy evidence && Matches(target)
            ? evidence.Enforcement
            : NetworkPolicyEnforcement.Unknown;

    public bool AllowsWorkloads(DeploymentTarget target) =>
        this is AttestedNetworkPolicy evidence && Matches(target) &&
        target.Capabilities.NetworkPolicyCapabilities.CanManage &&
        (evidence.Enforcement == NetworkPolicyEnforcement.Supported || evidence.AllowIsolationNotEnforced);
}

[BsonDiscriminator(nameof(UnattestedNetworkPolicy))]
public sealed record UnattestedNetworkPolicy : DeploymentTargetNetworkPolicy;

/// <summary>Operator evidence bound to one cluster and credential version, not API discovery.</summary>
[BsonDiscriminator(nameof(AttestedNetworkPolicy))]
public sealed record AttestedNetworkPolicy(
    NetworkPolicyEnforcement Enforcement,
    string ClusterIdentity,
    long ConnectionVersion,
    DateTime LastCheckedAtUtc,
    bool AllowIsolationNotEnforced) : DeploymentTargetNetworkPolicy;
