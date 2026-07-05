using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.DnsRecords.Events;

[EntityName("commit-domain-name")]
public sealed record CommitDomainNameEvent(
    string DnsRecordId,
    string ResourceId,
    EventCore EventCore
) : IPoleEvent;

