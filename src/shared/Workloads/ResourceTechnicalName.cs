// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Workloads;

/// <summary>Stable, operator-assigned technical identity, distinct from a mutable display label.</summary>
public static class ResourceTechnicalName
{
    public const int MaximumLength = 63;
    public const string Pattern = "^[a-z](?:[a-z0-9-]*[a-z0-9])?$";

    public static bool IsValid(string? value) => value is { Length: > 0 and <= MaximumLength } &&
        char.IsAsciiLetterLower(value[0]) && char.IsAsciiLetterOrDigit(value[^1]) && value.All(character =>
            char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');

    public static string Require(string value) => IsValid(value)
        ? value
        : throw new ArgumentException(
            "A technical name must be a lowercase DNS label beginning with a letter.",
            nameof(value));
}
