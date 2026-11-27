// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace Pole.Cli.Tests;

public sealed class CliConsoleTextTests
{
    [Fact]
    public void Safe_value_replaces_terminal_control_characters_and_truncates_long_values()
    {
        Assert.Equal("tenant?name", CliConsoleText.SafeValue("tenant\nname"));
        Assert.Equal(new string('x', 96) + "…", CliConsoleText.SafeValue(new string('x', 100)));
    }
}
