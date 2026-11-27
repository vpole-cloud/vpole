// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.SourceBuilds.Domain;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(AvailableSourceBuildExecution), typeof(ClaimedSourceBuildExecution))]
public closed record SourceBuildExecution;

public sealed record AvailableSourceBuildExecution(
    long Attempt
) : SourceBuildExecution;

public sealed record ClaimedSourceBuildExecution(
    string WorkerId,
    long Attempt,
    DateTime ExpiresAtUtc
) : SourceBuildExecution;
