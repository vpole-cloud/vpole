using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

internal static class DeploymentTargetReferenceFilters
{
    public static FilterDefinition<DeploymentTargetReference> ForTarget(
        string deploymentTargetId) => Builders<DeploymentTargetReference>.Filter.Eq(
            reference => reference.DeploymentTargetId,
            deploymentTargetId);

    public static FilterDefinition<DeploymentTargetReference> DomainZonesForTarget(
        string deploymentTargetId) => Builders<DeploymentTargetReference>.Filter.And(
            Builders<DeploymentTargetReference>.Filter.Eq(
                reference => reference.DeploymentTargetId,
                deploymentTargetId),
            Builders<DeploymentTargetReference>.Filter.Eq(
                reference => reference.OwnerType,
                DeploymentTargetReferenceOwnerTypes.DomainZone));

    public static FilterDefinition<DeploymentTargetReference> OwnedReferencesForTargets(
        IReadOnlyCollection<string> deploymentTargetIds,
        DeploymentTargetReferenceOwner owner) => Builders<DeploymentTargetReference>.Filter.And(
            Builders<DeploymentTargetReference>.Filter.In(
                reference => reference.DeploymentTargetId,
                deploymentTargetIds),
            Builders<DeploymentTargetReference>.Filter.Eq(
                reference => reference.OwnerType,
                owner.Type.Trim()),
            Builders<DeploymentTargetReference>.Filter.Eq(
                reference => reference.OwnerId,
                owner.Id.Trim()));

    public static FilterDefinition<DeploymentTargetReference> ByOwner(
        string deploymentTargetId,
        DeploymentTargetReferenceOwner owner) => Builders<DeploymentTargetReference>.Filter.And(
            Builders<DeploymentTargetReference>.Filter.Eq(
                reference => reference.DeploymentTargetId,
                deploymentTargetId),
            Builders<DeploymentTargetReference>.Filter.Eq(
                reference => reference.OwnerType,
                owner.Type.Trim()),
            Builders<DeploymentTargetReference>.Filter.Eq(
                reference => reference.OwnerId,
                owner.Id.Trim()));
}
