using Pole.Features.Domains.Application.Mongo;
using Pole.Features.Domains.Application.Lifecycle;
using Pole.Features.Domains.Application.HostnameBindings;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Application;
using Pole.Features.Domains.Domain;
using Pole.Features.Domains.Events;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Domains.Application.Common;

internal static class DomainMetadataEventExtensions
{
    public static DomainMetadataChangedEvent ToChangedEvent(this TenantDomainZone zone, string? bindingId = null) =>
        new(zone.Id, zone.Revision, bindingId)
        {
            EventCore = EventCore.Create(zone.TenantId)
        };
}
