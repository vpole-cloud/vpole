// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Functions.Domain;

public static class FunctionCronExpression
{
    private static readonly (int Min, int Max)[] FieldRanges =
    [
        (0, 59),
        (0, 23),
        (1, 31),
        (1, 12),
        (0, 7),
    ];

    public static bool IsValid(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == FieldRanges.Length &&
            fields.Select((field, index) => IsValidField(field, FieldRanges[index])).All(valid => valid);
    }

    private static bool IsValidField(string field, (int Min, int Max) range) =>
        field.Length > 0 && field.Split(',').All(part => IsValidPart(part, range));

    private static bool IsValidPart(string part, (int Min, int Max) range)
    {
        var stepParts = part.Split('/');
        if (stepParts.Length > 2 ||
            (stepParts.Length == 2 && !TryReadNumber(stepParts[1], 1, range.Max - range.Min + 1, out _)))
            return false;

        var value = stepParts[0];
        if (value == "*")
            return true;

        var rangeParts = value.Split('-');
        if (rangeParts.Length == 1)
            return TryReadNumber(rangeParts[0], range.Min, range.Max, out _);
        if (rangeParts.Length != 2 || !TryReadNumber(rangeParts[0], range.Min, range.Max, out var start) ||
            !TryReadNumber(rangeParts[1], range.Min, range.Max, out var end))
            return false;

        return start <= end;
    }

    private static bool TryReadNumber(
        string value,
        int min,
        int max,
        out int parsed) => int.TryParse(value, out parsed) && parsed >= min && parsed <= max;
}
