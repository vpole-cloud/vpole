// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database.Observation;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;
using Pole.Features.StorageProfiles.Domain.Observation;

namespace Pole.Features.StorageProfiles.Application.Mongo;

internal static class StorageProfileFilters
{
    public static FilterDefinition<StorageProfile> ByTechnicalName(string name) =>
        Builders<StorageProfile>.Filter.Eq(profile => profile.TechnicalName, name);

    public static FilterDefinition<StorageProfile> All() => Builders<StorageProfile>.Filter.Empty;

    public static FilterDefinition<StorageProfile> ById(string storageProfileId) =>
        Builders<StorageProfile>.Filter.Eq(profile => profile.Id, storageProfileId);

    public static FilterDefinition<StorageProfile> AtSnapshot(StorageProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Builders<StorageProfile>.Filter.And(
            ById(profile.Id),
            Builders<StorageProfile>.Filter.Eq(candidate => candidate.Revision, profile.Revision),
            Builders<StorageProfile>.Filter.Eq(candidate => candidate.ResolutionFence, profile.ResolutionFence));
    }

    public static FilterDefinition<StorageProfile> AtPolicy(string id, long revision) => ById(id) &
        Builders<StorageProfile>.Filter.Eq(profile => profile.Revision, revision) &
        Builders<StorageProfile>.Filter.Lt(profile => profile.ResolutionFence, long.MaxValue);

    public static FilterDefinition<StorageProfile> Default() =>
        Builders<StorageProfile>.Filter.Eq(profile => profile.IsDefault, true);

    public static FilterDefinition<StorageProfile> OtherDefaultsExcept(string storageProfileId) =>
        Builders<StorageProfile>.Filter.And(
            Default(),
            Builders<StorageProfile>.Filter.Ne(profile => profile.Id, storageProfileId));

    public static FilterDefinition<StorageProfile> AvailableForTarget(string deploymentTargetId) =>
        Builders<StorageProfile>.Filter.And(
            Builders<StorageProfile>.Filter.Eq(profile => profile.IsEnabled, true),
            Builders<StorageProfile>.Filter.ElemMatch(
                profile => profile.TargetMappings,
                mapping => mapping.DeploymentTargetId == deploymentTargetId));

    public static FilterDefinition<StorageProfile> AvailableAtResolution(StorageProfileResolutionFenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mapping = Builders<StorageProfileTargetMapping>.Filter.And(
            Builders<StorageProfileTargetMapping>.Filter.Eq(
                candidate => candidate.DeploymentTargetId,
                request.DeploymentTargetId),
            Builders<StorageProfileTargetMapping>.Filter.Eq(
                candidate => candidate.StorageClassName,
                request.StorageClassName));
        return Builders<StorageProfile>.Filter.And(
            ById(request.StorageProfileId),
            Builders<StorageProfile>.Filter.Eq(profile => profile.Revision, request.StorageProfileRevision),
            Builders<StorageProfile>.Filter.Eq(profile => profile.IsEnabled, true),
            Builders<StorageProfile>.Filter.ElemMatch(profile => profile.TargetMappings, mapping),
            Builders<StorageProfile>.Filter.Lt(profile => profile.ResolutionFence, long.MaxValue));
    }
}

internal static class StorageObservationFilters
{
    internal static FilterDefinition<StorageObservationControlDocument> ForControlScope(MongoObservationScope scope) =>
        Builders<StorageObservationControlDocument>.Filter.Eq(item => item.Id, scope.Id);

    internal static FilterDefinition<StorageObservationDemandDocument> ForDemandScope(MongoObservationScope scope) =>
        Builders<StorageObservationDemandDocument>.Filter.Eq(item => item.DeploymentTargetId, scope.Id);

    internal static FilterDefinition<StorageObservationSnapshotDocument>
        ForSnapshotScope(MongoObservationScope scope) =>
        Builders<StorageObservationSnapshotDocument>.Filter.Eq(item => item.Id, scope.Id);

    internal static FilterDefinition<StorageObservationControlDocument> ForControlContext(
        StorageObservationContext context) => Builders<StorageObservationControlDocument>.Filter.Eq(
        item => item.ConnectionVersion,
        context.ConnectionVersion);

    internal static FilterDefinition<StorageObservationSnapshotDocument> ForSnapshotContext(
        StorageObservationContext context) => Builders<StorageObservationSnapshotDocument>.Filter.Eq(
        item => item.ConnectionVersion,
        context.ConnectionVersion);
}

internal static class StorageProfileRevisionFilters
{
    public static FilterDefinition<StorageProfileRevision> Identity(string id, long revision) =>
        Builders<StorageProfileRevision>.Filter.Eq(item => item.Id, $"{id}/{revision}");
}
