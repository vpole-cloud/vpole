using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.RedisServices.Application.Common;

public static class RedisResourceDeployment
{
    public static ResourceTypeId ResourceType { get; } =
        ResourceTypeId.Create("pole/redis");
}
