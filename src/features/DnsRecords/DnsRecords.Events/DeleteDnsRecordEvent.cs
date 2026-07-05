using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.DnsRecords.Events;

[EntityName("delete-dns-record")]
public sealed record DeleteDnsRecordEvent(
    string Id,
    EventCore EventCore
) : IPoleEvent;

