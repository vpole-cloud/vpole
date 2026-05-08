using System.Collections.Immutable;
using Pole.Domain.Models;
using Pole.Features.Core.Models;

namespace Pole.Features.RedisServices.Common;

public sealed record RedisDto(
    string Id,
    string Name,
    string ResourceGroupId,
    RedisSpecDto Spec,
    ImmutableHashSet<string> Tags
);

public sealed record RedisSpecDto(
    StorageDto Storage,
    ResourcesDto Resources
);

public sealed record StorageDto(
    int SizeGb,
    string StorageClassName
);

public static class RedisDtoExtensions
{
    public static RedisDto ToDto(this Redis redis) => new(
        redis.Id,
        redis.Name,
        redis.ResourceGroupId,
        redis.Spec.ToDto(),
        redis.Tags
    );

    private static RedisSpecDto ToDto(this RedisSpec spec) => new(
        spec.Storage.ToDto(),
        spec.Resources.ToDto()
    );

    private static StorageDto ToDto(this Storage storage) => new(
        storage.SizeGb,
        storage.StorageClassName
    );
    
    public static RedisSpec ToDomain(this RedisSpecDto dto, string @namespace) => new(
        @namespace,
        dto.Storage.ToDomain(),
        dto.Resources.ToDomain()
    );
    
    private static Storage ToDomain(this StorageDto dto) => new(
        dto.SizeGb,
        dto.StorageClassName
    );
}
