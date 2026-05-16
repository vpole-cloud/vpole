using Pole.Features.Events.Abstractions;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;

namespace Pole.Features.WebServices.Common;

internal static class WebServicesEventExtensions
{
    extension(WebService webService)
    {
        internal CreateEvent ToCreateEvent()
            => new CreateEvent(
                webService.Id,
                webService.Name,
                webService.ResourceGroupId,
                webService.Spec,
                webService.DnsRecord,
                webService.Status,
                webService.Tags
            )
            {
                EventCore = EventCore.Create(webService.TenantId)
            };

        internal DeleteEvent ToDeleteEvent()
            => new DeleteEvent(
                webService.Id,
                webService.Name,
                webService.ResourceGroupId,
                webService.DnsRecord.Id,
                webService.Spec,
                webService.Tags)
            {
                EventCore = EventCore.Create(webService.TenantId)
            };
    }

    extension(CreateEvent createEvent)
    {
        internal WebService ToWebService()
            => WebServiceFactory.Create(
                createEvent.Name,
                createEvent.EventCore.TenantId,
                createEvent.ResourceGroupId,
                createEvent.Spec,
                createEvent.DnsRecord,
                WebServiceFactory.DefaultOptions with
                {
                    Id = createEvent.WebServiceId,
                    Status = createEvent.Status,
                    Tags = createEvent.Tags
                });
    }
}
