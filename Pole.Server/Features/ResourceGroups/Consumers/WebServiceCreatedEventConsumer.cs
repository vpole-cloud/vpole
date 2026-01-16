using MassTransit;
using MongoDB.Driver;
using Pole.Server.Database;
using Pole.Server.Features.ResourceGroups.Common;
using Pole.Server.Features.WebServices.Events;

namespace Pole.Server.Features.ResourceGroups.Consumers;

public class WebServiceCreatedEventConsumer(
    MongoDbContext dbContext
    ) : IConsumer<CreateEvent>
{
    public async Task Consume(ConsumeContext<CreateEvent> context)
    {
        var webService = await dbContext.WebServices
            .Find(ws => ws.Id == context.Message.WebServiceId)
            .FirstOrDefaultAsync(context.CancellationToken);

        if (webService is null)
            return;

        await dbContext.ResourceGroups.UpdateOneAsync(
            rg => rg.Id == webService.ResourceGroupId,
            Builders<ResourceGroup>.Update.Push(rg => rg.Resources, new ShortResourceInfo(
                Id: webService.Id,
                Name: webService.Name,
                Type: ResourceType.WebService
            )),
            cancellationToken: context.CancellationToken);
    }
}

public static class ResourceType
{
    public const string WebService = "WebService";
}