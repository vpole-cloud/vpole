using Pole.Features.Shared.Models;

namespace Pole.Features.RedisServices.Events;

public sealed record RedisServiceSpecSnapshot(
    string KubernetesNamespace,
    RedisServiceStorageSnapshot Storage,
    Resources Resources);

public sealed record RedisServiceStorageSnapshot(
    int SizeGb,
    string StorageClassName);
