using Pole.Server.Features.Common.Abstractions;

namespace Pole.Server.Features.DomainNames.Common;

public record DnsRecord(
    string Name,
    ResourceType ResourceType,
    string ResourceId,
    string ResourceName,
    string GroupId
) : BaseEntity;

public enum ResourceType
{
    WebService,
}