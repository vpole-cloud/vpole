using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceConnections.Application.Common;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Application.References;
using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application.Mongo;

internal static class ResourceConnectionTombstoneFilters
{
    public static FilterDefinition<ResourceConnectionCleanupTombstone> ByTenantAndId(
        string tenantId,
        string connectionId) =>
        Builders<ResourceConnectionCleanupTombstone>.Filter.And(
            Builders<ResourceConnectionCleanupTombstone>.Filter.Eq(
                item => item.TenantId,
                tenantId),
            Builders<ResourceConnectionCleanupTombstone>.Filter.Eq(
                item => item.Id,
                connectionId));

    public static FilterDefinition<ResourceConnectionCleanupTombstone> AfterId(string? id) =>
        id is null
            ? Builders<ResourceConnectionCleanupTombstone>.Filter.Empty
            : Builders<ResourceConnectionCleanupTombstone>.Filter.Gt(item => item.Id, id);
}

internal static class ResourceConnectionFilters
{
    public static FilterDefinition<ResourceConnection> ByName(string tenantId, string name) =>
        ByTenant(tenantId) & Builders<ResourceConnection>.Filter.Eq(item => item.Name, name);

    public static FilterDefinition<ResourceConnection> AtPlanningVersion(
        string tenantId, ResourceConnectionPlanningVersion version) =>
        AtRevision(tenantId, version.ConnectionId, version.Revision) &
        Builders<ResourceConnection>.Filter.Eq(item => item.ReferenceFence, version.ReferenceFence);

    public static FilterDefinition<ResourceConnection> All() =>
        Builders<ResourceConnection>.Filter.Empty;

    public static FilterDefinition<ResourceConnection> AfterId(string? id) =>
        id is null
            ? All()
            : Builders<ResourceConnection>.Filter.Gt(item => item.Id, id);

    public static FilterDefinition<ResourceConnection> ByTenant(string tenantId) =>
        Builders<ResourceConnection>.Filter.Eq(item => item.TenantId, tenantId);

    public static FilterDefinition<ResourceConnection> BySource(string tenantId, ResourceTypeId sourceType, string sourceId) =>
        ByTenant(tenantId) & Builders<ResourceConnection>.Filter.Eq(item => item.SourceResourceTypeId, sourceType.Value) &
        Builders<ResourceConnection>.Filter.Eq(item => item.SourceResourceId, sourceId);

    public static FilterDefinition<ResourceConnection> ByTenantAndId(
        string tenantId,
        string connectionId) =>
        Builders<ResourceConnection>.Filter.And(
            ByTenant(tenantId),
            Builders<ResourceConnection>.Filter.Eq(item => item.Id, connectionId));

    public static FilterDefinition<ResourceConnection> AtRevision(
        string tenantId,
        string connectionId,
        long revision) =>
        Builders<ResourceConnection>.Filter.And(
            ByTenantAndId(tenantId, connectionId),
            Builders<ResourceConnection>.Filter.Eq(item => item.Revision, revision));

    public static FilterDefinition<ResourceConnection> ByResource(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId) =>
        Builders<ResourceConnection>.Filter.And(
            ByTenant(tenantId),
            Builders<ResourceConnection>.Filter.Or(
                Builders<ResourceConnection>.Filter.And(
                    Builders<ResourceConnection>.Filter.Eq(
                        item => item.SourceResourceTypeId,
                        resourceType.Value),
                    Builders<ResourceConnection>.Filter.Eq(
                        item => item.SourceResourceId,
                        resourceId)),
                Builders<ResourceConnection>.Filter.And(
                    Builders<ResourceConnection>.Filter.Eq(
                        item => item.DestinationResourceTypeId,
                        resourceType.Value),
                    Builders<ResourceConnection>.Filter.Eq(
                        item => item.DestinationResourceId,
                        resourceId))));
}
