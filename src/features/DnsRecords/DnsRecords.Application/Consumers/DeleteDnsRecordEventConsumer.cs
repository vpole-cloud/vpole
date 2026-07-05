using MassTransit;
using Pole.Features.DnsRecords.Application.Common;
using Pole.Features.DnsRecords.Events;

namespace Pole.Features.DnsRecords.Application.Consumers;

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


