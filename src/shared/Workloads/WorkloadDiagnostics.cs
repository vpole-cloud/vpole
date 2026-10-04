using System.Text.Json.Serialization;

namespace Pole.Workloads;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AvailableWorkloadDiagnostics), "Available")]
[JsonDerivedType(typeof(UnavailableWorkloadDiagnostics), "Unavailable")]
public closed record WorkloadDiagnostics;
public sealed record UnavailableWorkloadDiagnostics(string Reason) : WorkloadDiagnostics;
public sealed record AvailableWorkloadDiagnostics(
    IReadOnlyList<DiagnosticWorkloadInstance> Instances,
    bool InstancesTruncated,
    IReadOnlyList<DiagnosticWorkloadEvent> Events,
    bool EventsTruncated,
    string? EventsUnavailableReason) : WorkloadDiagnostics;
public sealed record DiagnosticWorkloadContainer(
    string Name,
    string Kind,
    bool PreviousAvailable);
public sealed record DiagnosticWorkloadInstance(
    string Uid,
    string Name,
    string Phase,
    bool Ready,
    bool CurrentGeneration,
    int RestartCount,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<DiagnosticWorkloadContainer> Containers,
    bool ContainersTruncated);
public sealed record DiagnosticWorkloadEvent(
    string ObjectKind,
    string ObjectName,
    string Reason,
    int Count,
    DateTime? LastSeenAtUtc);
public sealed record RuntimeLogSelection(
    string InstanceUid,
    string Container,
    bool Previous,
    bool Follow,
    int TailLines);

public closed record RuntimeLogOpenResult;
public sealed record UnavailableRuntimeLog(string Reason) : RuntimeLogOpenResult;
public sealed record OpenRuntimeLog(RuntimeLogSource Source) : RuntimeLogOpenResult;

/// <summary>Owns this read's response/stream only; the shared target client remains cached.</summary>
public sealed class RuntimeLogSource(Stream output, Func<CancellationToken, Task<string?>> verify, Action? release = null) : IAsyncDisposable
{
    public Stream Output { get; } = output;
    public Task<string?> VerifyAsync(CancellationToken cancellationToken) => verify(cancellationToken);
    public async ValueTask DisposeAsync()
    {
        try { await Output.DisposeAsync(); }
        finally { release?.Invoke(); }
    }
}
