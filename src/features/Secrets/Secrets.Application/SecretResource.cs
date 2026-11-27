// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Workloads;

namespace Pole.Features.Secrets.Application;

internal static class SecretResource
{
    internal static readonly ResourceTypeId Type = ResourceTypeId.Create("pole/secret");
}
