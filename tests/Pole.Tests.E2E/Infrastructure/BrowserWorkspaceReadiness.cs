// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

internal static class BrowserWorkspaceReadiness
{
    public static async Task AssertAsync(
        IPage page,
        string resourceId,
        string heading,
        string? section = null)
    {
        await page.WaitForURLAsync(url =>
            QueryHelpers.ParseQuery(new Uri(url).Query)["resource"].ToString() == resourceId);
        var content = page.Locator("#app-content");
        await Assertions.Expect(
                content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = heading, Exact = true }).First)
            .ToBeVisibleAsync();
        Assert.Equal(resourceId, QueryHelpers.ParseQuery(new Uri(page.Url).Query)["resource"].ToString());
        if (section is not null)
            await Assertions
                .Expect(
                    content.Locator("[data-testid='section-tabs']:visible")
                        .GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = section, Exact = true }))
                .ToHaveAttributeAsync("aria-selected", "true");
    }
}
