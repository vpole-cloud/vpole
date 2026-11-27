// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Functions.Domain;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Functions.Application.Common;

public static class FunctionSecretReferences
{
    public static IReadOnlyList<ConsumerSecretKeyReference> From(FunctionConfiguration configuration) => configuration
        .SecretReferences
        .Values
        .Select(value => new ConsumerSecretKeyReference(value.SecretId, value.Key))
        .Distinct()
        .ToArray();
}

public sealed record FunctionSecretReferenceRejected(
    ConsumerSecretReferenceError Reason
);
