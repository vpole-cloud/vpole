// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;

namespace Pole.Workloads;

/// <summary>A validated TCP or UDP network port.</summary>
public readonly record struct NetworkPort
{
    private NetworkPort(int value) => Value = value;

    public int Value { get; }

    public static NetworkPort Create(int value) => value is >= 1 and <= 65535
        ? new NetworkPort(value)
        : throw new ArgumentOutOfRangeException(nameof(value), value, "Network port must be between 1 and 65535.");

    public static bool TryCreate(int value, out NetworkPort port)
    {
        if (value is >= 1 and <= 65535)
        {
            port = new NetworkPort(value);
            return true;
        }

        port = default;
        return false;
    }

    public static bool TryParse(string? value, out NetworkPort port)
    {
        port = default;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
            TryCreate(parsed, out port);
    }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
