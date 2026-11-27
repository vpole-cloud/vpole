// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.GitOps.Domain;

public sealed record GitAcceptedReference(
    [property: BsonId] string Id,
    GitSourceIdentity Source,
    string ConsumerResourceId,
    GitResourceIdentity Target
);
