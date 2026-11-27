// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Secrets.Contracts;

public static class SecretKeyReferenceRules
{
    public const int MaximumKeys = 64;
    public const int MaximumKeyLength = 128;

    public static bool ValidKey(string? key) => key is { Length: > 0 and <= MaximumKeyLength } &&
        key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');
}
