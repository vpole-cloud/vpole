// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Projections;

internal sealed record SecretProjectionMutationRecorded(
    SecretProjection Projection
);

internal sealed record SecretProjectionMutationSkipped;

internal union SecretProjectionMutationOutcome(SecretProjectionMutationRecorded, SecretProjectionMutationSkipped);
