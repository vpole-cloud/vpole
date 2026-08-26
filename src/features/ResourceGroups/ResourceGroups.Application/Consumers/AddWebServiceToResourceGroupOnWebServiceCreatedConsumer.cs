using MassTransit;
using Pole.Features.ResourceGroups.Application.Persistence;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;
using Pole.Features.WebServices.Events;

namespace Pole.Features.ResourceGroups.Application.Consumers;

public class AddWebServiceToResourceGroupOnWebServiceCreatedConsumer(
    RegisterResourceGroupWebService registerResourceGroupWebService)
    : IConsumer<WebServiceCreatedEvent>
{
    public async Task Consume(ConsumeContext<WebServiceCreatedEvent> context)
    {
        await registerResourceGroupWebService(
            context.Message.EventCore.TenantId,
            context.Message.ResourceGroupId,
            context.Message.WebServiceId,
            context.Message.Name,
            context.CancellationToken);
    }
}
