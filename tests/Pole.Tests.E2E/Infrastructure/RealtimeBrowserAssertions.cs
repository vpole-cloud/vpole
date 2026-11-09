using Microsoft.Playwright;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

internal static class RealtimeBrowserAssertions
{
    public static async Task WaitForInitialBootstrapAsync(RealtimeBrowserJournal journal, CancellationToken ct)
    {
        // Playwright can lose the preceding document's response body after a full navigation.
        await TestWait.UntilAsync(_ => Task.FromResult(!journal.Bootstraps.IsEmpty || journal.ParseFailures > 0),
            TimeSpan.FromSeconds(30),
            "The login bootstrap response was not recorded before navigation.",
            ct);
        await journal.DrainAsync();
        Assert.True(journal.ParseFailures == 0, journal.DescribeParseFailures());
    }

    public static async Task AssertNoReadableCredentialsAsync(IPage page, RealtimeBrowserJournal journal)
    {
        // Only a boolean crosses the browser boundary; no token/cookie/storage value is exported.
        var safe = await page.EvaluateAsync<bool>("""
            () => {
              const jwt = /eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/;
              const cookie = /PoleRealtime\.Dev|__Secure-PoleRealtime/;
              const values = [location.href, document.cookie];
              // Cookie Path would hide even a non-HttpOnly cookie at the UI root. Check its
              // actual path synchronously, keeping the current workspace and history state.
              const originalUrl = location.href;
              const originalState = history.state;
              try {
                history.replaceState(originalState, '', '/api/v1/realtime/session');
                values.push(document.cookie);
              } finally {
                history.replaceState(originalState, '', originalUrl);
              }
              for (const storage of [localStorage, sessionStorage]) {
                for (let index = 0; index < storage.length; index++) {
                  const key = storage.key(index);
                  values.push(key, storage.getItem(key) ?? '');
                }
              }
              return values.every(value => !jwt.test(value) && !cookie.test(value));
            }
            """);
        Assert.True(safe && journal.UrlsSafe,
            "JWTs and realtime cookies must be absent from JavaScript-readable storage, cookies and request URLs.");
    }

}
