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
            );

        internal DeleteEvent ToDeleteEvent()
            => new DeleteEvent(
                webService.Id,
                webService.Name,
                webService.ResourceGroupId,
                webService.DnsRecord.Id,
                webService.Spec,
                webService.Tags);
    }

    extension(CreateEvent createEvent)
    {
        internal WebService ToWebService()
            => WebServiceFactory.Create(
                createEvent.Name,
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