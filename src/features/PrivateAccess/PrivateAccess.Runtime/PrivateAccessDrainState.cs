// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.PrivateAccess.Runtime;

public sealed class PrivateAccessDrainState
{
    private readonly CancellationTokenSource deadline = new();
    private int draining;

    public bool IsDraining => Volatile.Read(ref draining) != 0;
    public CancellationToken Deadline => deadline.Token;

    public void Begin()
    {
        if (Interlocked.Exchange(ref draining, 1) != 0)
            return;
        _ = EndAfterGraceAsync();
    }

    private async Task EndAfterGraceAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        await deadline.CancelAsync();
    }
}
