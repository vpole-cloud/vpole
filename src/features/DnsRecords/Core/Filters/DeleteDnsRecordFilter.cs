using MassTransit;
using Pole.Feature.DnsRecords.Events;
using Pole.Features.Events.Abstractions;

namespace Pole.Feature.DnsRecords.Core.Filters;

public class DeleteDnsRecordFilter<T>(
) : IFilter<ConsumeContext<T>> where T : class, IDeleteDnsRecordEvent
{
    public async Task Send(
        ConsumeContext<T> context,
        IPipe<ConsumeContext<T>> next)
    {
        await next.Send(context);

        await context.Publish(
            new DeleteDnsRecordEvent(context.Message.DnsRecordId));
    }

    public void Probe(ProbeContext context)
    {
        context.CreateFilterScope("delete-dns-record-filter");
    }
}
