// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;

namespace Pole.Validation;

public static class WorkloadValidationExtensions
{
    public static IRuleBuilderOptions<T, string?> ResourceTypeId<T>(
        this IRuleBuilder<T, string?> ruleBuilder,
        string displayName) => ruleBuilder.Must(value => Workloads.ResourceTypeId.TryCreate(value, out _))
        .WithMessage($"{displayName} is invalid.");
}
