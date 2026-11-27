// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Secrets.Application;

public sealed record SecretNameAlreadyExists;

public sealed record SecretReferenceRejected;

public sealed record SecretEncryptionFailed;

public sealed record SecretMissing;

public sealed record SecretVersionConflict;

public sealed record SecretMappedKeysRequired;

public sealed record SecretBindingLimitReached;

public sealed record SecretConsumerAlreadyBound;

public sealed record SecretUnknownKeys;

public sealed record SecretBindingsRemain;

public sealed record SecretManagedByProvider;

public sealed record SecretGitOwned;
