namespace Pole.Features.Events.Abstractions;

public interface IDeleteDnsRecordEvent : IPoleEvent
{
    string DnsRecordId { get; }
}
