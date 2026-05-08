using MassTransit;
using MongoDB.Driver;
using Pole.Database;
using Pole.Feature.DnsRecords.Events;

namespace Pole.Feature.DomainNames.Consumers;

public class DeleteDnsRecordEventConsumer(
    MongoDbContext dbContext
) : IConsumer<DeleteDnsRecordEvent>
{
    public async Task Consume(ConsumeContext<DeleteDnsRecordEvent> context)
    {
        await dbContext.DnsRecords.DeleteOneAsync(x => x.Id == context.Message.Id);
    }
}