// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text.RegularExpressions;

namespace Pole.Features.Backups.Application.Operations;

public static partial class BackupRestoreTime
{
    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?(?:Z|[+-]\d\d:\d\d)$")]
    private static partial Regex ZonedTimestamp();

    public static bool TryTime(string text, out DateTimeOffset time)
    {
        time = default;
        return ZonedTimestamp().IsMatch(text) &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out time) &&
            time.UtcTicks % TimeSpan.TicksPerMicrosecond == 0;
    }
}
