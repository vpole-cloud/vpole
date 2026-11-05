using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Application.References;
using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceConnections.Events;

namespace Pole.Features.ResourceConnections.Application.Common;

internal static class ResourceConnectionEventExtensions
{
    public static ResourceConnectionChangedEvent ToChangedEvent(this ResourceConnection connection) =>
        new(connection.Id, connection.Revision, connection.Generation)
        {
            EventCore = EventCore.Create(connection.TenantId)
        };
}
