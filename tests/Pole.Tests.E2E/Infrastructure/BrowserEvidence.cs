// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.CompilerServices;
using Microsoft.Playwright;
using Pole.Testing;

namespace Pole.Tests.E2E.Infrastructure;

internal static class BrowserEvidence
{
    public static async Task<IPage> NewPageAsync(IBrowserContext context, [CallerMemberName] string scenario = "")
    {
        var page = await context.NewPageAsync();
        var journal = TestPhaseJournal.Current ?? new TestPhaseJournal(scenario);
        var observer = new BrowserPhaseJournal(page, journal);
        page.Close += (_, _) => observer.Dispose();
        return page;
    }
}
