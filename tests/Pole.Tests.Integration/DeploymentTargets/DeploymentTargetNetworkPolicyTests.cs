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
    [InlineData(NetworkPolicyEnforcement.Unknown, false, NetworkPolicyReadiness.EnforcementRequired, false)]
    [InlineData(NetworkPolicyEnforcement.Unsupported, false, NetworkPolicyReadiness.EnforcementRequired, false)]
    [InlineData(NetworkPolicyEnforcement.Supported, false, NetworkPolicyReadiness.EnforcementAttested, true)]
    [InlineData(NetworkPolicyEnforcement.Unknown, true, NetworkPolicyReadiness.IsolationNotEnforced, true)]
    [InlineData(NetworkPolicyEnforcement.Unsupported, true, NetworkPolicyReadiness.IsolationNotEnforced, true)]
    public void AdmissionAndProjection_AgreeWithoutClaimingIsolation(
        NetworkPolicyEnforcement enforcement,
        bool allow,
        NetworkPolicyReadiness readiness,
        bool admitted)
    {
        var target = Target();
        target = Attest(target, enforcement, allow);
        var dto = target.ToNetworkPolicyDto();
        Assert.Equal(readiness, dto.Readiness);
        Assert.Equal(enforcement.ToString(), dto.Enforcement.ToString());
        Assert.Equal(NetworkPolicyEvidenceSource.OperatorAttestation, dto.EvidenceSource);
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
        Assert.Equal(NetworkPolicyEnforcementStatus.Unknown, target.ToNetworkPolicyDto().Enforcement);
        Assert.Equal(NetworkPolicyEvidenceSource.None, target.ToNetworkPolicyDto().EvidenceSource);
        Assert.False(DeploymentTargetAdmissionSupport.AdmitForWorkload(target).TryGetValue(out _, out _));
    }

    [Fact]
    public void AttestationIsPerTarget_AndInvalidatedByCredentialOrClusterChanges()
    {
        var first = Attest(Target(), NetworkPolicyEnforcement.Supported, false);
        var second = Target();
        Assert.Equal(NetworkPolicyEnforcementStatus.Unknown, second.ToNetworkPolicyDto().Enforcement);
        var rotated = first.ReplaceConnectionCredential(
            first.ClusterIdentity,
            DeploymentTargetConnectionFactory.CreateInCluster(2), first.UpdatedAtUtc.AddMinutes(1));
        Assert.Equal(NetworkPolicyEnforcementStatus.Unknown, rotated.ToNetworkPolicyDto().Enforcement);
        Assert.Equal(NetworkPolicyReadiness.EnforcementRequired, rotated.ToNetworkPolicyDto().Readiness);
        Assert.Equal(
            NetworkPolicyEnforcementStatus.Unknown,
            (first with { ClusterIdentity = second.ClusterIdentity }).ToNetworkPolicyDto().Enforcement);
        var overridden = Attest(first, NetworkPolicyEnforcement.Unsupported, true);
        Assert.False((overridden with { Connection = rotated.Connection }).ToNetworkPolicyDto().OverrideEnabled);
    }

    [Fact]
    public void AttestationOwnsEvidenceAndAdvancesBothConfigurationRevisions()
    {
        var target = Target();
        var checkedAtUtc = target.UpdatedAtUtc.AddMinutes(1);
        var attested = target.AttestNetworkPolicy(NetworkPolicyEnforcement.Supported, false, checkedAtUtc);
        var evidence = Assert.IsType<AttestedNetworkPolicy>(attested.WorkloadConfiguration.NetworkPolicy);

        Assert.IsType<UnattestedNetworkPolicy>(target.WorkloadConfiguration.NetworkPolicy);
        Assert.Equal(target.ClusterIdentity, evidence.ClusterIdentity);
        Assert.Equal(target.Connection.Version, evidence.ConnectionVersion);
        Assert.Equal(checkedAtUtc, evidence.LastCheckedAtUtc);
        Assert.Equal(target.Revision + 1, attested.Revision);
        Assert.Equal(target.WorkloadConfiguration.Revision + 1, attested.WorkloadConfiguration.Revision);
        Assert.Equal(checkedAtUtc, attested.UpdatedAtUtc);
        Assert.Throws<ArgumentException>(() => target.AttestNetworkPolicy(
            NetworkPolicyEnforcement.Supported, false, target.UpdatedAtUtc.AddSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => target.AttestNetworkPolicy(
            (NetworkPolicyEnforcement)99, false, checkedAtUtc));
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
        target.AttestNetworkPolicy(enforcement, allow, target.UpdatedAtUtc);
}
