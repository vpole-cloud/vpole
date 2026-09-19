using MongoDB.Bson;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence.Admission;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

public sealed class DeploymentTargetNetworkPolicyTests
{
    [Theory]
    [InlineData(NetworkPolicyEnforcement.Unknown, false, "EnforcementRequired", false)]
    [InlineData(NetworkPolicyEnforcement.Unsupported, false, "EnforcementRequired", false)]
    [InlineData(NetworkPolicyEnforcement.Supported, false, "EnforcementAttested", true)]
    [InlineData(NetworkPolicyEnforcement.Unknown, true, "IsolationNotEnforced", true)]
    [InlineData(NetworkPolicyEnforcement.Unsupported, true, "IsolationNotEnforced", true)]
    public void AdmissionAndProjection_AgreeWithoutClaimingIsolation(
        NetworkPolicyEnforcement enforcement,
        bool allow,
        string readiness,
        bool admitted)
    {
        var target = Target();
        target = Attest(target, enforcement, allow);
        var dto = target.ToNetworkPolicyDto();
        Assert.Equal(readiness, dto.Readiness);
        Assert.Equal(enforcement.ToString(), dto.Enforcement);
        Assert.Equal("OperatorAttestation", dto.EvidenceSource);
        Assert.Equal(target.CreatedAtUtc, dto.LastCheckedAtUtc);
        Assert.Equal(
            admitted,
            DeploymentTargetAdmissionSupport.AdmitForWorkload(target) is Ok<DeploymentTargetLookupDto>);
        Assert.Equal(
            admitted,
            DeploymentTargetAdmissionSupport.AdmitDefaultForNewWorkload(target) is Ok<DeploymentTargetLookupDto>);
    }

    [Fact]
    public void HealthyApiAndNamespacePermissions_AreNotEnforcementEvidence()
    {
        var target = Target();
        Assert.Equal("Unknown", target.ToNetworkPolicyDto().Enforcement);
        Assert.Equal("None", target.ToNetworkPolicyDto().EvidenceSource);
        Assert.False(DeploymentTargetAdmissionSupport.AdmitForWorkload(target).TryGetValue(out _, out _));
    }

    [Fact]
    public void AttestationIsPerTarget_AndInvalidatedByCredentialOrClusterChanges()
    {
        var first = Attest(Target(), NetworkPolicyEnforcement.Supported, false);
        var second = Target();
        Assert.Equal("Unknown", second.ToNetworkPolicyDto().Enforcement);
        var rotated = first.ReplaceConnectionCredential(
            first.ClusterIdentity,
            DeploymentTargetConnectionFactory.CreateInCluster(2), first.UpdatedAtUtc.AddMinutes(1));
        Assert.Equal("Unknown", rotated.ToNetworkPolicyDto().Enforcement);
        Assert.Equal("EnforcementRequired", rotated.ToNetworkPolicyDto().Readiness);
        Assert.Equal(
            "Unknown",
            (first with { ClusterIdentity = second.ClusterIdentity }).ToNetworkPolicyDto().Enforcement);
        var overridden = Attest(first, NetworkPolicyEnforcement.Unsupported, true);
        Assert.False((overridden with { Connection = rotated.Connection }).ToNetworkPolicyDto().OverrideEnabled);
    }

    internal static DeploymentTarget Target()
    {
        var id = ObjectId.GenerateNewId().ToString();
        var now = DateTime.UtcNow;
        return DeploymentTargetFactory.Create(
            "Target",
            $"cluster:{id}",
            DeploymentTargetConnectionFactory.CreateInCluster(1),
            DeploymentTargetHealth.Healthy(1, now),
            DeploymentTargetCapabilities.Observed(
                1,
                now,
                "v1.35.0",
                new(true, true, true, true, true)),
            DeploymentTargetWorkloadConfiguration.Create(null),
            true,
            true,
            new(id, now));
    }

    internal static DeploymentTarget Attest(
        DeploymentTarget target,
        NetworkPolicyEnforcement enforcement,
        bool allow) =>
        target with
        {
            WorkloadConfiguration = target.WorkloadConfiguration with
            {
                NetworkPolicy = new(
                    enforcement,
                    target.ClusterIdentity,
                    target.Connection.Version,
                    target.CreatedAtUtc,
                    allow)
            }
        };
}
