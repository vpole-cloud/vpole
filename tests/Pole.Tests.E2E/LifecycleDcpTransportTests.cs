using System.Reflection;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class LifecycleDcpTransportTests
{
    [Fact]
    public async Task CurrentModelResource_ResolvesDcpInstanceAndCapturesOwnedConsoleOutput()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var directory = Path.Combine(Path.GetTempPath(), "pole-dcp-lifecycle-" + Guid.NewGuid().ToString("N"));
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions
            {
                Args = [],
                AssemblyName = Assembly.Load("Pole.AppHost").FullName,
                DisableDashboard = true,
            });
        const string message
            = "RuntimeCell native absence confirmed: TenantId=507f1f77bcf86cd799439011, RuntimeCellId=507f1f77bcf86cd799439012, Generation=2, CheckSequence=1, ObservedAtUtc=2026-11-09T12:00:00.1234567Z";
        var resource = builder.AddExecutable(
            "lifecycle-emitter",
            "/bin/sh",
            Path.GetTempPath(),
            "-c",
            "trap 'exit 0' TERM INT; printf '%s\\n' \"$1\"; while :; do sleep 1; done",
            "lifecycle-child",
            message);
        var application = builder.Build();
        var cleanup = new TestResourceScope("lifecycle DCP transport", TimeSpan.FromSeconds(15));
        cleanup.Defer(
            "remove diagnostic evidence",
            _ =>
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
                return Task.CompletedTask;
            });
        cleanup.Defer("dispose diagnostic application", _ => application.DisposeAsync().AsTask());
        cleanup.Defer("stop diagnostic application", token => application.StopAsync(token));
        await cleanup.RunAsync(async () =>
        {
            await application.StartAsync(budget.Token);
            await application.ResourceNotifications.WaitForResourceAsync(
                resource.Resource.Name,
                KnownResourceStates.Running,
                budget.Token);
            Assert.True(application.ResourceNotifications.TryGetCurrentState(resource.Resource.Name, out var running));
            Assert.NotNull(running);
            Assert.NotEqual(resource.Resource.Name, running.ResourceId);
            var logger = application.Services.GetRequiredService<ResourceLoggerService>();
            await using var collector = new LifecycleEvidence(directory);
            collector.Start(
                LifecycleEvidence.ResourceLines(logger, resource.Resource, live: false),
                LifecycleEvidence.ResourceLines(logger, resource.Resource, live: true));
            var path = Path.Combine(directory, "lifecycle-events.jsonl");
            await TestWait.UntilAsync(
                async token =>
                {
                    var content = await File.ReadAllTextAsync(path, token);
                    return content.EndsWith('\n') && content.Length > 0;
                },
                TimeSpan.FromSeconds(10),
                "The owned DCP console message was not captured.",
                budget.Token);
            await collector.DisposeAsync();
            Assert.All(
                await File.ReadAllLinesAsync(path, budget.Token),
                line =>
                {
                    using var json = JsonDocument.Parse(line);
                    Assert.Equal("runtime-cell", json.RootElement.GetProperty("component").GetString());
                    Assert.Equal("native-absent", json.RootElement.GetProperty("phase").GetString());
                    Assert.DoesNotContain("507f1f77bcf86cd799439011", line);
                });
        });
    }
}
