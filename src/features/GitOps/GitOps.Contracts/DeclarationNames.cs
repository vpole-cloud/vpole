// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Workloads;

namespace Pole.Features.GitOps.Contracts;

public static class DeclarationNames
{
    public static bool Valid(string? value) => ResourceTechnicalName.IsValid(value);
}
