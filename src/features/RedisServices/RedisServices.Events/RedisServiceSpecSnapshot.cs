using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.RedisServices.Events;

public sealed record RedisServiceSpecSnapshot(
    string KubernetesNamespace,
    RedisServiceStorageSnapshot Storage,
    Resources Resources);

public sealed record RedisServiceStorageSnapshot(
    int SizeGb,
    ResolvedStorageProfile StorageProfile);
