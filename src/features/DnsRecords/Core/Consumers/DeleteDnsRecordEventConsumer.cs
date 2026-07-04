using MassTransit;
using Pole.Feature.DnsRecords.Core.Common;
using Pole.Feature.DnsRecords.Events;

namespace Pole.Feature.DnsRecords.Core.Consumers;

public class DeleteDnsRecordEventConsumer(
    IDomainNameReservationService reservationService
) : IConsumer<DeleteDnsRecordEvent>
{
    public async Task Consume(ConsumeContext<DeleteDnsRecordEvent> context)
    {
        await Handle(context.Message, context.CancellationToken);
    }

    public async Task Handle(
        DeleteDnsRecordEvent message,
        CancellationToken cancellationToken = default)
    {
        await reservationService.ReleaseAsync(
            new ReleaseDomainNameRequest(
                message.EventCore.TenantId,
                message.Id),
            cancellationToken);
    }
}
