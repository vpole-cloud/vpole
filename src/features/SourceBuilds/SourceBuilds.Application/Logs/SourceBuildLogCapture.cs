// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Logging;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Logs;

public delegate Task<SourceBuildLogCapture> CreateSourceBuildLogCapture(SourceBuildJob job);

public sealed class SourceBuildLogCaptureFactory(
    StartSourceBuildLog start,
    AppendSourceBuildLog append,
    RenewSourceBuildLog renew,
    SealSourceBuildLog seal,
    SourceBuildLogOptions options,
    TimeProvider timeProvider,
    ILogger<SourceBuildLogCapture> logger
)
{
    public async Task<SourceBuildLogCapture> CreateAsync(SourceBuildJob job)
    {
        var log = SourceBuildLogFactory.Create(
            job.Id,
            job.TenantId,
            job.WebServiceId,
            Guid.NewGuid().ToString("N"),
            options.Limits,
            timeProvider.GetUtcNow().UtcDateTime);
        var enabled = false;
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            enabled = (await start(log, budget.Token)).Value is SourceBuildLogWriteAccepted;
        }
        catch (Exception)
        {
            logger.LogWarning("Log capture could not start for build {BuildJobId}.", job.Id);
        }

        return new SourceBuildLogCapture(log, enabled, append, renew, seal, logger);
    }
}

/// <summary>One job's bounded writer. Diagnostics failure never blocks pipe drainage or changes the build outcome.</summary>
public sealed class SourceBuildLogCapture : IAsyncDisposable
{
    private readonly AppendSourceBuildLog append;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Task heartbeat;
    private readonly SourceBuildLogIdentity identity;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ILogger logger;
    private readonly int maxEntryBytes;
    private readonly RenewSourceBuildLog renew;
    private readonly SealSourceBuildLog seal;
    private long sequence;
    private bool started;
    private bool truncated;
    private bool unavailable;

    public SourceBuildLogCapture(
        SourceBuildLog log,
        bool started,
        AppendSourceBuildLog append,
        RenewSourceBuildLog renew,
        SealSourceBuildLog seal,
        ILogger logger)
    {
        identity = new SourceBuildLogIdentity(log.TenantId, log.WebServiceId, log.Id, log.WriterToken);
        maxEntryBytes = log.Limits.MaxEntryBytes;
        sequence = log.LastSequence;
        this.started = started;
        this.append = append;
        this.renew = renew;
        this.seal = seal;
        this.logger = logger;
        heartbeat = started ? RenewLoopAsync() : Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await heartbeat;
        await gate.WaitAsync();
        try
        {
            // Start's response can be lost after an acknowledged insert. Seal only this writer
            // identity if it exists; a missing/fenced capture cannot alter another worker's log.
            if (started || !unavailable)
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await seal(identity, unavailable || !started, budget.Token);
                }
                catch (Exception)
                {
                    MarkUnavailable();
                }
            }

            started = false;
        }
        finally
        {
            gate.Release();
            gate.Dispose();
            lifetime.Dispose();
        }
    }

    public async Task WriteAsync(
        SourceBuildLogStream stream,
        string sanitizedText,
        CancellationToken ct = default)
    {
        // Reader cancellation belongs to HTTP reads; a worker capture has its own small IO budget.
        await gate.WaitAsync();
        try
        {
            var offset = 0;
            while (started && !unavailable && !truncated && offset < sanitizedText.Length)
            {
                var chunk = SourceBuildLog.Utf8Prefix(sanitizedText[offset..], maxEntryBytes);
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    var result = await append(identity, sequence + 1, stream, chunk, budget.Token);
                    if (result.Value is not SourceBuildLogWriteAccepted accepted)
                    {
                        unavailable = true;
                        break;
                    }

                    sequence = accepted.Log.LastSequence;
                    truncated = accepted.Log.Truncated;
                    offset += chunk.Length;
                }
                catch (Exception)
                {
                    MarkUnavailable();
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RenewLoopAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), lifetime.Token);
                await gate.WaitAsync(lifetime.Token);
                try
                {
                    if (unavailable)
                        return;
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    budget.CancelAfter(TimeSpan.FromSeconds(5));
                    if ((await renew(identity, budget.Token)).Value is SourceBuildLogWriteFenced)
                    {
                        unavailable = true;
                        return;
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    MarkUnavailable();
                    return;
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    public void MarkUnavailable()
    {
        if (!unavailable)
            logger.LogWarning("Log capture storage became unavailable for build {BuildJobId}.", identity.JobId);
        unavailable = true;
    }
}
