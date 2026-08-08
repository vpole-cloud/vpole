namespace Pole.Features.RedisServices.Events;

public sealed record RedisServiceSpecSnapshot(
    RedisServiceStorageSnapshot Storage);

public sealed record RedisServiceStorageSnapshot(
    int SizeGb,
    string StorageProfileId);
