// ReSharper disable MemberCanBePrivate.Global

using Pole.Server.Features.WebServices.Common;

namespace Pole.Server.Features.RedisServices.Common;

public sealed record RedisDto(
    string Id,
    string Name,
    string ResourceGroupId,
    RedisSpecDto Spec
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
        redis.Spec.ToDto()
    );
    
    public static RedisSpecDto ToDto(this RedisSpec spec) => new(
        spec.Storage.ToDto(),
        spec.Resources.ToDto()
    );
    
    public static StorageDto ToDto(this Storage storage) => new(
        storage.SizeGb,
        storage.StorageClassName
    );
    
    public static RedisSpec ToDomain(this RedisSpecDto dto) => new(
        dto.Storage.ToDomain(),
        dto.Resources.ToDomain()
    );
    
    public static Storage ToDomain(this StorageDto dto) => new(
        dto.SizeGb,
        dto.StorageClassName
    );
}
