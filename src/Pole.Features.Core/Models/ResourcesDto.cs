using Pole.Domain.Models;

namespace Pole.Features.Core.Models;

public record ResourcesDto(
    ResourceDto Requests,
    ResourceDto Limits
);
public record ResourceDto(
    int CpuCores,
    int MemoryMi
);

public static class ResourcesDtoExtensions
{
    public static ResourcesDto ToDto(this Resources resources) => new(
        resources.Requests.ToDto(),
        resources.Limits.ToDto()
    );
    
    public static ResourceDto ToDto(this Resource resource) => new(
        resource.CpuCores,
        resource.MemoryMi
    );
    
    public static Resources ToDomain(this ResourcesDto dto) => new(
        dto.Requests.ToDomain(),
        dto.Limits.ToDomain()
    );
    
    public static Resource ToDomain(this ResourceDto dto) => new(
        dto.CpuCores,
        dto.MemoryMi
    );
}