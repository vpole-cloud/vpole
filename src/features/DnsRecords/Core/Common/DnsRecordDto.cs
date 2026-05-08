using Pole.Domain.Models;

namespace Pole.Feature.DnsRecords.Core.Common;

public sealed record DnsRecordDto(
    string Id,
    string Domain,
    ResourceInfoDto Resource
);

public sealed record ResourceInfoDto(
    string Id,
    string Name,
    ResourceType ResourceType,
    string GroupId
);

public static class DnsRecordDtoExtensions
{
    public static DnsRecordDto ToDto(this DnsRecord dn) => new(
        dn.Id,
        dn.Domain,
        new ResourceInfoDto(
            dn.Resource.Id,
            dn.Resource.Name,
            dn.Resource.ResourceType,
            dn.Resource.GroupId
            )
    );
}
