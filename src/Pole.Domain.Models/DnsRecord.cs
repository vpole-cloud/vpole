using Pole.Domain.Models.Abstractions;

namespace Pole.Domain.Models;

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