using MassTransit;
using Pole.Feature.DnsRecords.Core.Common;
using Pole.Feature.DnsRecords.Events;
using Pole.ResultType;

namespace Pole.Feature.DnsRecords.Core.Consumers;

public class CommitDomainNameEventConsumer(
    IDomainNameReservationService reservationService)
    : IConsumer<CommitDomainNameEvent>
{
    public async Task Consume(ConsumeContext<CommitDomainNameEvent> context)
    {
        await Handle(context.Message, context.CancellationToken);
    }

    public async Task Handle(
        CommitDomainNameEvent message,
        CancellationToken cancellationToken = default)
    {
        var result = await reservationService.CommitAsync(
            new CommitDomainNameRequest(
                message.EventCore.TenantId,
                message.DnsRecordId,
                message.ResourceId),
            cancellationToken);

        if (result is Failure failure)
            throw new InvalidOperationException(failure.Message);
    }
}
