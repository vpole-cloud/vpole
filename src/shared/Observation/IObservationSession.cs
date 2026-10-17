namespace Pole.Observation;

public interface IObservationReadBudget
{
    Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken = default);
}

public abstract record ObservationChange;

public sealed record ObservationSourceReady(string SourceId) : ObservationChange;

public sealed record ObservationSourceLost : ObservationChange;

public sealed record ObservationInvalidated(string? SourceId = null) : ObservationChange;

public sealed record ObservationCut(
    long SourceVersion,
    string SourceId);

public enum ObservationSampleResult
{
    Accepted,
    Superseded,
    BindingLost,
    Unavailable,
    OwnerLost
}

public enum ObservationSampleMetric
{
    Accepted,
    Rejected,
    Cancelled,
    Failed
}

/// <summary>The feature owns native reads, context validation, persistence outcomes and metrics.</summary>
public interface IObservationSession
{
    IAsyncEnumerable<ObservationChange> WatchAsync(CancellationToken ct);
    Task<long?> SetSourceAsync(long version, string? sourceId, CancellationToken ct);
    Task<ObservationSampleResult> SampleAsync(ObservationCut cut, CancellationToken ct);
    Task<bool> RenewAsync(CancellationToken ct);
    void RecordSource(bool healthy);
    void RecordSample(ObservationSampleMetric outcome, TimeSpan elapsed);
}
