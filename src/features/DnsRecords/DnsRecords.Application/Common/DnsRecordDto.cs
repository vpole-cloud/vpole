using Pole.Features.DnsRecords.Domain;
using Pole.Features.Shared.Models;

namespace Pole.Features.DnsRecords.Application.Common;

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

