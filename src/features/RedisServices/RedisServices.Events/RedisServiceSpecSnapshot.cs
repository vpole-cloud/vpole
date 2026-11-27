// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.RedisServices.Events;

public sealed record RedisServiceSpecSnapshot(
    RedisServiceStorageSnapshot Storage
);

public sealed record RedisServiceStorageSnapshot(
    int SizeGb,
    string StorageProfileId
);
