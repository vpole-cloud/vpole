// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.ResultType;

/// <summary>
///     Represents a successful typed operation that has no data to return.
/// </summary>
public readonly record struct Unit
{
    public static Unit Value => default;
}
