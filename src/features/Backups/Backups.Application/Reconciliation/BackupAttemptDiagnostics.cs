// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Logging;
using Pole.Features.Backups.Domain;
using Pole.Observability;

namespace Pole.Features.Backups.Application.Reconciliation;

internal static class BackupAttemptDiagnostics
{
    internal static async Task RunAsync(
        ILogger logger,
        BackupExecutionOrigin origin,
        string tenantId,
        string policyId,
        string identity,
        Func<Task> attempt,
        CancellationToken ct)
    {
        using var trace = TraceContext.Push(TraceContext.Create(origin.TraceId, origin.CorrelationId));
        using var scope = logger.BeginScope(
            new Dictionary<string, object>
            {
                ["TenantId"] = tenantId,
                ["BackupPolicyId"] = policyId,
                ["BackupWorkId"] = identity,
                ["TraceId"] = origin.TraceId,
                ["CorrelationId"] = origin.CorrelationId,
            });
        logger.LogDebug("Continuing durable backup work {BackupWorkId}", identity);
        try
        {
            await attempt();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Native exception messages can include response payloads. Retain the stack and identity,
            // while leaving credential values and native bodies out of the diagnostic.
            logger.LogError(
                "Backup work {BackupWorkId} interrupted: {ExceptionType}; {StackTrace}",
                identity,
                exception.GetType().FullName,
                exception.StackTrace);
            throw;
        }
    }
}
