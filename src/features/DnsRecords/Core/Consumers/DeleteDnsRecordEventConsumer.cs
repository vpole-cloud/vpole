using MassTransit;
using MongoDB.Driver;
using Pole.Feature.DnsRecords.Core.Common;
using Pole.Feature.DnsRecords.Events;

namespace Pole.Feature.DnsRecords.Core.Consumers;

public class DeleteDnsRecordEventConsumer(
    IDnsRecordDbContext dbContext
) : IConsumer<DeleteDnsRecordEvent>
{
    public async Task Consume(ConsumeContext<DeleteDnsRecordEvent> context)
    {
        await dbContext.DnsRecords.DeleteOneAsync(x => x.Id == context.Message.Id && x.TenantId == context.Message.EventCore.TenantId);
    }
}
