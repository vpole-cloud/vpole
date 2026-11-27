// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Hangfire;
using Pole.Features.SourceBuilds.Application.Logs;

namespace Pole.Features.SourceBuilds.Application.Jobs;

public sealed class SourceBuildLogRecoveryJob(
    RecoverSourceBuildLogs recover
)
{
    [AutomaticRetry(Attempts = 0)]
    public Task RecoverAsync() => recover();
}
