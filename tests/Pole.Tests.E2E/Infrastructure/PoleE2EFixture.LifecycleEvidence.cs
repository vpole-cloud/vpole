using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Pole.Tests.E2E.Infrastructure;

public sealed partial class PoleE2EFixture
{
    private LifecycleEvidence? _lifecycleEvidence;

    private void StartLifecycleEvidence()
    {
        var directory = Environment.GetEnvironmentVariable("POLE_TEST_TIMING_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        _lifecycleEvidence = new LifecycleEvidence(directory);
        var logger = _application!.Services.GetService<ResourceLoggerService>();
        if (logger is not null)
            _lifecycleEvidence.Start(LifecycleLines(logger, live: false), LifecycleLines(logger, live: true));
    }

    private async Task StopLifecycleEvidenceAsync()
    {
        if (_lifecycleEvidence is not null)
            await _lifecycleEvidence.DisposeAsync();
    }

    private static async IAsyncEnumerable<string> LifecycleLines(
        ResourceLoggerService logger,
        bool live,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var batches = live ? logger.WatchAsync("pole-server") : logger.GetAllAsync("pole-server");
        await foreach (var batch in batches.WithCancellation(cancellationToken))
            foreach (var line in batch)
                yield return line.Content;
    }
}
