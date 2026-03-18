using Pole.Domain.Models;

namespace Pole.Features.DomainNames.Common;

public sealed record DnsRecordDto(
    string Id,
    string Name,
    ResourceType ResourceType,
    string ResourceId,
    string ResourceName,
    string GroupId
);

public static class DnsRecordDtoExtensions
{
    public static DnsRecordDto ToDto(this DnsRecord dn) => new(
        dn.Id,
        dn.Name,
        dn.ResourceType,
        dn.ResourceId,
        dn.ResourceName,
        dn.GroupId
    );
}
