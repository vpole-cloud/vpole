using MongoDB.Driver;
using Pole.Encryption;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

internal static class DeploymentTargetFilters
{
    public static FilterDefinition<DeploymentTarget> All() => Builders<DeploymentTarget>.Filter.Empty;

    public static FilterDefinition<DeploymentTarget> AfterId(string? cursor) => cursor is null
            ? All()
            : Builders<DeploymentTarget>.Filter.Gt(target => target.Id, cursor);

    public static FilterDefinition<DeploymentTarget> ById(string targetId) => Builders<DeploymentTarget>.Filter.Eq(target => target.Id, targetId);

    public static FilterDefinition<DeploymentTarget> NotPendingDeletion() => Builders<DeploymentTarget>.Filter.Eq(
            target => target.DeletionRequestedAtUtc,
            null);

    public static FilterDefinition<DeploymentTarget> AtRevision(
        DeploymentTarget target) => Builders<DeploymentTarget>.Filter.And(
            Builders<DeploymentTarget>.Filter.Eq(item => item.Id, target.Id),
            Builders<DeploymentTarget>.Filter.Eq(item => item.Revision, target.Revision));

    public static FilterDefinition<DeploymentTarget> AtRevisionAndReferenceFence(
        DeploymentTarget target) => Builders<DeploymentTarget>.Filter.And(
            AtRevision(target),
            Builders<DeploymentTarget>.Filter.Eq(
                item => item.ReferenceFence,
                target.ReferenceFence));

    public static FilterDefinition<DeploymentTarget> AtRevisionAndConnectionVersion(
        DeploymentTarget target) => Builders<DeploymentTarget>.Filter.And(
            AtRevision(target),
            Builders<DeploymentTarget>.Filter.Eq(
                item => item.Connection.Version,
                target.Connection.Version));

    public static FilterDefinition<DeploymentTarget> AtKubeconfigEnvelope(
        string targetId,
        long connectionVersion,
        EncryptedValue envelope) => Builders<DeploymentTarget>.Filter.And(
            ById(targetId),
            Builders<DeploymentTarget>.Filter.Eq(
                "Connection.Version",
                connectionVersion),
            Builders<DeploymentTarget>.Filter.Eq(
                "Connection.EncryptedKubeconfig",
                envelope));

    public static FilterDefinition<DeploymentTarget> AtServiceAccountCredentialEnvelope(
        string targetId,
        long connectionVersion,
        EncryptedValue envelope) => Builders<DeploymentTarget>.Filter.And(
            ById(targetId),
            Builders<DeploymentTarget>.Filter.Eq(
                "Connection.Version",
                connectionVersion),
            Builders<DeploymentTarget>.Filter.Eq(
                "Connection.EncryptedCredential",
                envelope));

    public static FilterDefinition<DeploymentTarget> AtRevisionAndNotPendingDeletion(
        DeploymentTarget target) => Builders<DeploymentTarget>.Filter.And(
            AtRevision(target),
            NotPendingDeletion());

    public static FilterDefinition<DeploymentTarget> AtReferenceFenceBeforeDeletion(
        DeploymentTarget target) => Builders<DeploymentTarget>.Filter.And(
            AtRevisionAndReferenceFence(target),
            Builders<DeploymentTarget>.Filter.Eq(
                item => item.DeletionRequestedAtUtc,
                null));

    public static FilterDefinition<DeploymentTarget> AtDeletionFence(
        DeploymentTarget target) => Builders<DeploymentTarget>.Filter.And(
            AtRevisionAndReferenceFence(target),
            Builders<DeploymentTarget>.Filter.Eq(
                item => item.DeletionRequestedAtUtc,
                target.DeletionRequestedAtUtc));

    private static FilterDefinition<DeploymentTarget> AcceptingConfigurationReferences() => Builders<DeploymentTarget>.Filter.And(
            Builders<DeploymentTarget>.Filter.Eq(target => target.Enabled, true),
            Builders<DeploymentTarget>.Filter.Eq(target => target.AcceptsWorkloads, true),
            NotPendingDeletion());

    public static FilterDefinition<DeploymentTarget> AcceptingNewWorkloads() => Builders<DeploymentTarget>.Filter.And(
            AcceptingConfigurationReferences(),
            NetworkPolicyAdmission());

    private static FilterDefinition<DeploymentTarget> NetworkPolicyAdmission() => Builders<DeploymentTarget>.Filter.Where(target => target.Health.FailureCode != "ClusterIdentityMismatch" &&
            target.Capabilities.NetworkPolicyCapabilities.CanGet &&
            target.Capabilities.NetworkPolicyCapabilities.CanCreate &&
            target.Capabilities.NetworkPolicyCapabilities.CanUpdate &&
            target.Capabilities.NetworkPolicyCapabilities.CanPatch &&
            target.Capabilities.NetworkPolicyCapabilities.CanDelete &&
            target.WorkloadConfiguration.NetworkPolicy is AttestedNetworkPolicy &&
            (((AttestedNetworkPolicy)target.WorkloadConfiguration.NetworkPolicy).Enforcement ==
                NetworkPolicyEnforcement.Supported ||
             ((AttestedNetworkPolicy)target.WorkloadConfiguration.NetworkPolicy).AllowIsolationNotEnforced) &&
            ((AttestedNetworkPolicy)target.WorkloadConfiguration.NetworkPolicy).ClusterIdentity ==
                target.ClusterIdentity &&
            ((AttestedNetworkPolicy)target.WorkloadConfiguration.NetworkPolicy).ConnectionVersion ==
                target.Connection.Version);

    // A private access session references an already applied deployment. Pausing new
    // workload admission does not remove access, but enforcement must stay attested.
    private static FilterDefinition<DeploymentTarget> ExistingPrivateAccessAdmission() =>
        Builders<DeploymentTarget>.Filter.Where(target =>
            target.Enabled &&
            target.DeletionRequestedAtUtc == null &&
            target.Health.FailureCode != "ClusterIdentityMismatch" &&
            target.Capabilities.NetworkPolicyCapabilities.CanGet &&
            target.Capabilities.NetworkPolicyCapabilities.CanCreate &&
            target.Capabilities.NetworkPolicyCapabilities.CanUpdate &&
            target.Capabilities.NetworkPolicyCapabilities.CanPatch &&
            target.Capabilities.NetworkPolicyCapabilities.CanDelete &&
            target.WorkloadConfiguration.NetworkPolicy is AttestedNetworkPolicy &&
            ((AttestedNetworkPolicy)target.WorkloadConfiguration.NetworkPolicy).Enforcement ==
                NetworkPolicyEnforcement.Supported &&
            ((AttestedNetworkPolicy)target.WorkloadConfiguration.NetworkPolicy).ClusterIdentity ==
                target.ClusterIdentity &&
            ((AttestedNetworkPolicy)target.WorkloadConfiguration.NetworkPolicy).ConnectionVersion ==
                target.Connection.Version);

    public static FilterDefinition<DeploymentTarget> WithConfiguredPublicBaseDomain() => Builders<DeploymentTarget>.Filter.And(
            Builders<DeploymentTarget>.Filter.Ne(
                target => target.WorkloadConfiguration.Ingress,
                null),
            Builders<DeploymentTarget>.Filter.Ne(
                target => target.WorkloadConfiguration.Ingress!.PublicBaseDomain,
                null));

    public static FilterDefinition<DeploymentTarget> FenceableWorkloadConfigurations(
        IReadOnlyCollection<DeploymentTargetWorkloadConfigurationFenceRequest> requests)
    {
        var expectedConfigurations = requests.Select(request => Builders<DeploymentTarget>.Filter.And(
                Builders<DeploymentTarget>.Filter.Eq(
                    target => target.Id,
                    request.DeploymentTargetId),
                Builders<DeploymentTarget>.Filter.Eq(
                    target => target.WorkloadConfiguration.Revision,
                    request.ExpectedWorkloadConfigurationRevision)));

        return Builders<DeploymentTarget>.Filter.And(
            Builders<DeploymentTarget>.Filter.Or(expectedConfigurations),
            AcceptingNewWorkloads(),
            Builders<DeploymentTarget>.Filter.Lt(
                target => target.ReferenceFence,
                long.MaxValue));
    }

    public static FilterDefinition<DeploymentTarget> FenceableReferences(
        IReadOnlyCollection<string> deploymentTargetIds,
        bool requireAdmission,
        DeploymentTargetReferenceOwner owner)
    {
        var filter = Builders<DeploymentTarget>.Filter.And(
            Builders<DeploymentTarget>.Filter.In(
                target => target.Id,
                deploymentTargetIds),
            Builders<DeploymentTarget>.Filter.Lt(
                target => target.ReferenceFence,
                long.MaxValue));

        return requireAdmission
            ? Builders<DeploymentTarget>.Filter.And(
                filter,
                // A profile configures future placement; it must exist before the operator
                // can attest a fresh target. Runtime references still fence workload admission.
                owner.Type switch
                {
                    DeploymentTargetReferenceOwnerTypes.ComputeProfile =>
                        AcceptingConfigurationReferences(),
                    DeploymentTargetReferenceOwnerTypes.PrivateAccessSession or
                        DeploymentTargetReferenceOwnerTypes.PrivateAccessGateway =>
                        ExistingPrivateAccessAdmission(),
                    _ => AcceptingNewWorkloads()
                })
            : filter;
    }
}
