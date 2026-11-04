using Pole.Features.GitOps.Contracts;
using System.Collections.Immutable;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.RedisServices.Domain;
using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.RedisServices.Application.Common;

public sealed record RedisDto(
    string Id,
    string Name,
    string ResourceGroupId,
    RedisSpecDto Spec,
    ImmutableHashSet<string> Tags,
    PlacementSummaryDto? Placement,
    long Revision,
    string Lifecycle,
    GitResourceStatus? GitOps
);

public sealed record RedisSpecDto(
    StorageDto Storage
);

public sealed record StorageDto(
    int SizeGb,
    string StorageProfileId,
    ResolvedStorageProfileDto? Profile = null
);

public static class RedisDtoExtensions
{
    public static RedisDto ToDto(
        this Redis redis,
        PlacementSummaryDto? placement = null) => new(
        redis.Id,
        redis.Name,
        redis.ResourceGroupId,
        redis.Spec.ToDto(),
        redis.Tags,
        placement,
        redis.Revision,
        redis.Lifecycle switch
        {
            PreparedRedisLifecycle => "Prepared",
            ActiveRedisLifecycle => "Active",
            DeletingRedisLifecycle => "Deleting",
            _ => throw new ArgumentOutOfRangeException(nameof(redis))
        },
        null
    );

    private static RedisSpecDto ToDto(this RedisSpec spec) => new(
        spec.Storage.ToDto()
    );

    private static StorageDto ToDto(this Storage storage) => new(
        storage.SizeGb,
        storage.StorageProfileId
    );

    public static RedisSpec ToDomain(this RedisSpecDto dto) => new(
        dto.Storage.ToDomain()
    );

    private static Storage ToDomain(this StorageDto dto) => new(
        dto.SizeGb,
        dto.StorageProfileId
    );
}
