using MassTransit;
using Pole.Features.DnsRecords.Events;
using Pole.Features.Events.Abstractions;
using Pole.Observability;

namespace Pole.Features.DnsRecords.Application.Filters;

/// <summary>
/// Publishes a follow-up DNS record delete event after a resource delete event has been consumed.
/// </summary>
public class DeleteDnsRecordFilter<T>(
) : IFilter<ConsumeContext<T>> where T : class
{
    public async Task Send(
        ConsumeContext<T> context,
        IPipe<ConsumeContext<T>> next)
    {
        await next.Send(context);

        if (context.Message is not IDeleteDnsRecordEvent deleteDnsRecordEvent)
        {
            return;
        }

        var poleEvent = context.Message as IPoleEvent;
        var carrier = context.Message as ITraceContextCarrier;
        var traceContext = TraceContext.Create(
            GetHeaderValue(context.Headers, TraceHeaders.TraceId) ?? poleEvent?.EventCore.TraceId ?? carrier?.TraceId ?? TraceContext.Current?.TraceId,
            GetHeaderValue(context.Headers, TraceHeaders.CorrelationId) ?? poleEvent?.EventCore.CorrelationId ?? carrier?.CorrelationId ?? TraceContext.Current?.CorrelationId);

        using var contextScope = TraceContext.Push(traceContext);

        await context.Publish(
            new DeleteDnsRecordEvent(deleteDnsRecordEvent.DnsRecordId, deleteDnsRecordEvent.EventCore));
    }

    public void Probe(ProbeContext context)
    {
        context.CreateFilterScope("delete-dns-record-filter");
    }

    private static string? GetHeaderValue(Headers headers, string headerName)
        => headers.TryGetHeader(headerName, out var value) ? value?.ToString() : null;
}


