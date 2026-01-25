using Pole.Server.Features.ResourceGroups.Common;
using Pole.Server.Features.WebServices.Common;

namespace Pole.Server.Features.RedisServices.Common;

public record Redis(
    string Name,
    string ResourceGroupId,
    string Namespace,
    RedisSpec Spec
) : PoleService(Name, ResourceGroupId, Namespace);

public record RedisSpec(
    Storage Storage,
    Resources Resources
);

public record Storage(
    int SizeGb,
    string StorageClassName
);
