using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using Pole.Features.ResourceDeployments.Application.Persistence.Observation;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class ResourceDeploymentObservationSerializationTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, null)]
    public async Task StoredHealthAndDiagnosticsRoundTripInFreshProcessWithoutSerializerWarmup(bool observed,
        bool? available)
    {
        var health = observed
            ? (WorkloadObservation)new ObservedWorkload(false,
                true,
                1,
                1,
                1,
                0,
                0,
                false,
                [new("pod-a", "Running", false, true, 1, ["CrashLoopBackOff"])],
                [new("BackOff", 1, DateTime.UnixEpoch)],
                null)
            : new UnavailableWorkloadObservation("Forbidden");
        WorkloadDiagnostics? diagnostics = available is null ? null
            : available.Value ? new AvailableWorkloadDiagnostics(
                [
                    new("pod-uid",
                        "pod-a",
                        "Running",
                        false,
                        true,
                        1,
                        ["CrashLoopBackOff"],
                        [new("app", "Application", true)],
                        false)
                ],
                false,
                [new("Pod", "pod-a", "BackOff", 1, DateTime.UnixEpoch)],
                false,
                null)
            : new UnavailableWorkloadDiagnostics("OwnershipNotVerified");
        var snapshot = new ObservationSnapshotDocument(ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            new("pole/web-service",
                ObjectId.GenerateNewId().ToString(),
                ObjectId.GenerateNewId().ToString(),
                ObjectId.GenerateNewId().ToString(),
                ObjectId.GenerateNewId().ToString(),
                1,
                2,
                3,
                ObjectId.GenerateNewId().ToString(),
                4,
                5,
                "owned-namespace"),
            "controller-uid",
            6,
            7,
            DateTime.UnixEpoch,
            ObservationValueMappings.Store(health),
            ObservationValueMappings.Store(diagnostics),
            "fingerprint",
            null);
        var bytes = snapshot.ToBson();
        var helper = Path.Combine(AppContext.BaseDirectory,
            "ObservationSerializationProbe",
            "Pole.Tests.ObservationSerializationProbe.dll");
        Assert.True(File.Exists(helper),
            "The approved cold-process probe must be built and copied with the integration project.");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(helper);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(Convert.ToBase64String(bytes));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, await error);
        using var result = JsonDocument.Parse(await output);
        Assert.Equal(observed ? "Observed" : "Unavailable",
            result.RootElement.GetProperty("ObservationKind").GetString());
        Assert.Equal(available is null ? "Absent" : available.Value ? "Available" : "Unavailable",
            result.RootElement.GetProperty("DiagnosticsKind").GetString());
        Assert.Equal(observed ? 1 : 0, result.RootElement.GetProperty("Pods").GetInt32());
        Assert.Equal(available == true ? 1 : 0, result.RootElement.GetProperty("Instances").GetInt32());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), result.RootElement.GetProperty("Hash").GetString());
    }

    [Fact]
    public void StoredPayloadsAreBoundedAndUnknownNativeReasonsPhasesAndKindsAreSanitized()
    {
        var observation = ObservationValueMappings.Store(new ObservedWorkload(true,
            true,
            1,
            1,
            1,
            1,
            1,
            false,
            Enumerable.Range(0, 105).Select(index => new ObservedWorkloadPod($"pod-{index}",
                "native-private-payload",
                true,
                true,
                1,
                ["native-private-payload", "CrashLoopBackOff"])).ToArray(),
            Enumerable.Range(0, 40).Select(_ => new ObservedWorkloadEvent("native-private-payload", 1, null)).ToArray(),
            "native-private-payload"));
        var diagnostics = ObservationValueMappings.Store(new AvailableWorkloadDiagnostics(
            Enumerable.Range(0, 105).Select(index => new DiagnosticWorkloadInstance($"uid-{index}",
                $"pod-{index}",
                "native-private-payload",
                true,
                true,
                1,
                ["native-private-payload"],
                Enumerable.Range(0, 80).Select(container =>
                    new DiagnosticWorkloadContainer($"app-{container}", "Application", false)).ToArray(),
                false)).ToArray(),
            false,
            Enumerable.Range(0, 40).Select(_ =>
                    new DiagnosticWorkloadEvent("native-private-payload", "pod-a", "native-private-payload", 1, null))
                .ToArray(),
            false,
            "native-private-payload"))!;
        Assert.Equal(100, observation.Observed!.Pods.Length);
        Assert.Equal(32, observation.Observed.Events.Length);
        Assert.True(observation.Observed.PodsTruncated);
        Assert.False(observation.Observed.Ready);
        Assert.False(observation.Observed.RolloutCurrent);
        Assert.All(observation.Observed.Pods, pod => Assert.Equal("Unknown", pod.Phase));
        Assert.Equal(100, diagnostics.Available!.Instances.Length);
        Assert.True(diagnostics.Available.InstancesTruncated);
        Assert.Equal(32, diagnostics.Available.Events.Length);
        Assert.True(diagnostics.Available.EventsTruncated);
        Assert.All(diagnostics.Available.Instances,
            instance =>
            {
                Assert.Equal(64, instance.Containers.Length);
                Assert.True(instance.ContainersTruncated);
            });
        Assert.DoesNotContain("native-private-payload", observation.ToBsonDocument().ToJson());
        Assert.DoesNotContain("native-private-payload", diagnostics.ToBsonDocument().ToJson());
        Assert.Equal("Unavailable",
            ObservationValueMappings.Store(new UnavailableWorkloadObservation("native-private-payload")).Reason);
        Assert.Equal("Unavailable",
            ObservationValueMappings.Store(new UnavailableWorkloadDiagnostics("native-private-payload"))!.Reason);
    }

    [Fact]
    public void NativeDescriptionsCannotSmuggleUrlsOrLogsThroughMetadataFields()
    {
        Assert.Throws<ArgumentException>(() => ObservationValueMappings.Store(new ObservedWorkload(false,
            false,
            1,
            0,
            0,
            0,
            0,
            false,
            [new("https://invalid-metadata", "Running", false, true, 0, [])],
            [],
            null)));
        var value = ObservationValueMappings.Store(new AvailableWorkloadDiagnostics(
            [new("owned-uid", "pod-a", "Running", true, true, 0, [], [new("debug", "Ephemeral", false)], false)],
            false,
            [],
            false,
            null))!;
        Assert.Empty(value.Available!.Instances[0].Containers);
    }
}
