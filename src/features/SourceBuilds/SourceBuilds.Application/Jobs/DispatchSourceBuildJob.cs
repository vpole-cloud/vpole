// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Hangfire;

namespace Pole.Features.SourceBuilds.Application.Jobs;

public delegate string DispatchSourceBuildJob(string buildJobId);

public sealed class HangfireSourceBuildJobDispatcher(
    IBackgroundJobClient backgroundJobs
)
{
    public string Invoke(string buildJobId) =>
        backgroundJobs.Enqueue<SourceBuildJobExecutor>(executor => executor.ExecuteAsync(buildJobId));
}
