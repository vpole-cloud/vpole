using MassTransit;
using MongoDB.Driver;
using Pole.Features.ResourceGroups.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Events;

namespace Pole.Features.ResourceGroups.Consumers;

public class WebServiceCreatedEventConsumer(IResourceGroupDbContext dbContext)
    : IConsumer<CreateEvent>
{
    public async Task Consume(ConsumeContext<CreateEvent> context)
    {
        var resourceInfo = new ShortResourceInfo(
            Id: context.Message.WebServiceId,
            Name: context.Message.Name,
            Type: ResourceType.WebService
        );

        var filter = Builders<ResourceGroup>.Filter.And(
            Builders<ResourceGroup>.Filter.Eq(rg => rg.Id, context.Message.ResourceGroupId),
            Builders<ResourceGroup>.Filter.Not(
                Builders<ResourceGroup>.Filter.ElemMatch(
                    rg => rg.Resources,
                    r => r.Id == resourceInfo.Id
                )
            )
        );

        var update = Builders<ResourceGroup>.Update.Push(
            rg => rg.Resources,
            resourceInfo
        );

        await dbContext.ResourceGroups.UpdateOneAsync(
            filter,
            update,
            cancellationToken: context.CancellationToken);
    }
}