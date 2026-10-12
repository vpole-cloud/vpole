using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceConnections.Events;

namespace Pole.Features.ResourceConnections.Application.Persistence;

internal static class ResourceConnectionEventExtensions
{
    public static ResourceConnectionChangedEvent ToChangedEvent(this ResourceConnection connection) =>
        new(connection.Id, connection.Revision, connection.Generation) { EventCore = EventCore.Create(connection.TenantId) };
}
