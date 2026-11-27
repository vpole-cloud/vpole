// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Outbox.Application;
using Xunit;

namespace Pole.Tests.Integration.Database;

public sealed class OutboxProcessorJobTests
{
    [Fact]
    public void ProcessOutboxMessages_PreventsConcurrentHangfireRuns()
    {
        var processMethod = typeof(OutboxProcessorJob).GetMethod(nameof(OutboxProcessorJob.ProcessOutboxMessagesAsync));

        Assert.NotNull(processMethod);
        Assert.Contains(
            processMethod.GetCustomAttributes(inherit: false),
            attribute => attribute.GetType().Name == "DisableConcurrentExecutionAttribute");
    }
}
