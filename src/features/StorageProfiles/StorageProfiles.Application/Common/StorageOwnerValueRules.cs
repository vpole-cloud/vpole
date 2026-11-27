// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text;
using MongoDB.Bson;

namespace Pole.Features.StorageProfiles.Application.Common;

internal static class StorageOwnerValueRules
{
    internal static string? MongoIdentity(string? value) => ObjectId.TryParse(value, out var id) ? id.ToString() : null;

    internal static string? ResourceIdentity(string? value) =>
        ObjectId.TryParse(value, out var id) ? id.ToString() : OptionalText(value, 253);

    internal static string? OptionalText(string? value, int maximum)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximum)
            return null;
        foreach (var rune in value.EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                    or UnicodeCategory.Surrogate || rune.Value == 0xfffd)
                return null;
        return value;
    }
}
