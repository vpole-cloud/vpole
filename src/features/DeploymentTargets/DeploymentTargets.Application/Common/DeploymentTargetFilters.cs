using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Common;

internal static class DeploymentTargetFilters
{
    public static FilterDefinition<DeploymentTarget> ById(string targetId) =>
        Builders<DeploymentTarget>.Filter.Eq(target => target.Id, targetId);

    public static FilterDefinition<DeploymentTarget> NotPendingDeletion() =>
        Builders<DeploymentTarget>.Filter.Eq(
            target => target.DeletionRequestedAtUtc,
            null);

    public static FilterDefinition<DeploymentTarget> AtRevision(
        DeploymentTarget target) =>
        Builders<DeploymentTarget>.Filter.And(
            Builders<DeploymentTarget>.Filter.Eq(item => item.Id, target.Id),
            Builders<DeploymentTarget>.Filter.Eq(item => item.Revision, target.Revision));

    public static FilterDefinition<DeploymentTarget> AtRevisionAndReferenceFence(
        DeploymentTarget target) =>
        Builders<DeploymentTarget>.Filter.And(
            AtRevision(target),
            Builders<DeploymentTarget>.Filter.Eq(
                item => item.ReferenceFence,
                target.ReferenceFence));

    public static FilterDefinition<DeploymentTarget> AtRevisionAndConnectionVersion(
        DeploymentTarget target) =>
        Builders<DeploymentTarget>.Filter.And(
            AtRevision(target),
            Builders<DeploymentTarget>.Filter.Eq(
                item => item.Connection.Version,
                target.Connection.Version));

    public static FilterDefinition<DeploymentTarget> AtRevisionAndNotPendingDeletion(
        DeploymentTarget target) =>
        Builders<DeploymentTarget>.Filter.And(
            AtRevision(target),
            NotPendingDeletion());

    public static FilterDefinition<DeploymentTarget> AcceptingNewWorkloads() =>
        Builders<DeploymentTarget>.Filter.And(
            Builders<DeploymentTarget>.Filter.Eq(target => target.Enabled, true),
            Builders<DeploymentTarget>.Filter.Eq(target => target.AcceptsWorkloads, true),
            NotPendingDeletion());

    public static FilterDefinition<DeploymentTarget> WithConfiguredPublicBaseDomain() =>
        Builders<DeploymentTarget>.Filter.And(
            Builders<DeploymentTarget>.Filter.Ne(
                target => target.WorkloadConfiguration.Ingress,
                null),
            Builders<DeploymentTarget>.Filter.Ne(
                target => target.WorkloadConfiguration.Ingress!.PublicBaseDomain,
                null));

    public static FilterDefinition<DeploymentTarget> FenceableWorkloadConfigurations(
        IReadOnlyCollection<DeploymentTargetWorkloadConfigurationFenceRequest> requests)
    {
        var expectedConfigurations = requests.Select(request =>
            Builders<DeploymentTarget>.Filter.And(
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
        bool requireAdmission)
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
                AcceptingNewWorkloads())
            : filter;
    }
}
