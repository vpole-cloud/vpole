using MassTransit;

namespace Pole.Feature.DnsRecords.Events;

[EntityName("delete-dns-record")]
public sealed record DeleteDnsRecordEvent(
    string Id
);