using System.Diagnostics;
using System.Diagnostics.Metrics;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Common;

public sealed class ResourceDeploymentMetrics : IDisposable
{
    public const string MeterName = "Pole.ResourceDeployments";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _attempts;
    private readonly Counter<long> _completions;
    private readonly Counter<long> _failures;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _staleClaimRecoveries;
    private readonly Counter<long> _runtimeCellWakeups;

    public ResourceDeploymentMetrics()
    {
        _attempts = _meter.CreateCounter<long>(
            "pole.resource_deployments.reconciliation.attempts");
        _completions = _meter.CreateCounter<long>(
            "pole.resource_deployments.reconciliation.completions");
        _failures = _meter.CreateCounter<long>(
            "pole.resource_deployments.reconciliation.failures");
        _duration = _meter.CreateHistogram<double>(
            "pole.resource_deployments.reconciliation.duration",
            unit: "s");
        _staleClaimRecoveries = _meter.CreateCounter<long>(
            "pole.resource_deployments.recovery.stale_claims");
        _runtimeCellWakeups = _meter.CreateCounter<long>(
            "pole.resource_deployments.runtime_cell.wakeups");
    }

    public void RecordAttempt(
        string resourceType,
        ResourceDeploymentOperation operation) =>
        _attempts.Add(1, Tags(resourceType, operation));

    public void RecordCompletion(
        string resourceType,
        ResourceDeploymentOperation operation,
        long startedTimestamp)
    {
        var tags = Tags(resourceType, operation);
        _completions.Add(1, tags);
        _duration.Record(
            Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds,
            tags);
    }

    public void RecordFailure(
        string resourceType,
        ResourceDeploymentOperation operation,
        long startedTimestamp)
    {
        var tags = Tags(resourceType, operation);
        _failures.Add(1, tags);
        _duration.Record(
            Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds,
            tags);
    }

    public void RecordStaleClaimRecoveries(long count)
    {
        if (count > 0)
            _staleClaimRecoveries.Add(count);
    }

    public void RecordRuntimeCellWakeups(long count)
    {
        if (count > 0)
            _runtimeCellWakeups.Add(count);
    }

    private static TagList Tags(
        string resourceType,
        ResourceDeploymentOperation operation) =>
        new()
        {
            { "resource.type", resourceType },
            {
                "operation",
                operation == ResourceDeploymentOperation.Apply
                    ? "apply"
                    : "delete"
            }
        };

    public void Dispose() => _meter.Dispose();
}
