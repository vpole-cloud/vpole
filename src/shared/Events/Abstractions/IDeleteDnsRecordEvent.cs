namespace Pole.Features.Events.Abstractions;

public interface IDeleteDnsRecordEvent
{
    string DnsRecordId { get; }
}