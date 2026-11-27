// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Tests.E2E.Infrastructure;

public sealed partial class PoleE2EFixture
{
    private readonly Lock _browserTransportLock = new();
    private Task<string>? _browserEndpoint;

    public Task<string> GetBrowserEndpointAsync(CancellationToken cancellationToken)
    {
        lock (_browserTransportLock)
        {
            _browserEndpoint ??= StartBrowserTransportAsync(cancellationToken);
            return _browserEndpoint.WaitAsync(cancellationToken);
        }
    }

    private async Task<string> StartBrowserTransportAsync(CancellationToken cancellationToken)
    {
        using var timing = _timings.Measure("fixture.e2e.browser.transport.start");
        var container = new PlaywrightBrowserContainer(Identity);
        // Register before startup so a partial failure still disposes the owned container.
        _cleanup.Add(("fixture.e2e.cleanup.browser.container", _ => container.DisposeAsync().AsTask()));
        await container.StartAsync(cancellationToken);
        return container.WebSocketEndpoint;
    }
}
