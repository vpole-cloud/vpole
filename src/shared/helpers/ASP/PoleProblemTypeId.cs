// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.ASP.Helpers;

/// <summary>
///     Creates stable, non-dereferenceable identifiers for problem types owned by Pole.
///     The tag authority date is intentionally fixed and must not vary by deployment.
/// </summary>
public static class PoleProblemTypeId
{
    private const string Prefix = "tag:pole.dev,2026:problem:";

    public static string FromCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (!IsValidCode(code))
            throw new ArgumentException(
                "A Pole problem code must contain lowercase ASCII dot-separated segments " +
                "whose characters are letters, digits, or hyphens.",
                nameof(code));

        return Prefix + code;
    }

    private static bool IsValidCode(string code) => code.Split('.')
        .All(segment => segment.Length > 0 && IsAsciiLetterOrDigit(segment[0]) && IsAsciiLetterOrDigit(segment[^1]) &&
            segment.All(character => IsAsciiLetterOrDigit(character) || character is '-'));

    private static bool IsAsciiLetterOrDigit(char character) => character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
