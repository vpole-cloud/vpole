using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Pole.Tests.E2E.Infrastructure;

public sealed partial class PoleE2EFixture
{
    private LifecycleEvidence? _lifecycleEvidence;

    private void StartLifecycleEvidence()
    {
        var directory = Environment.GetEnvironmentVariable("POLE_TEST_TIMING_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
            return;
        _lifecycleEvidence = new LifecycleEvidence(directory);
        var logger = _application!.Services.GetService<ResourceLoggerService>();
        var primary = _application.Services
            .GetService<DistributedApplicationModel>()
            ?.Resources
            .SingleOrDefault(resource => resource.Name == "pole-server");
        if (logger is not null && primary is not null)
            _lifecycleEvidence.Start(
                LifecycleEvidence.ResourceLines(logger, primary, live: false),
                LifecycleEvidence.ResourceLines(logger, primary, live: true));
    }

    private async Task StopLifecycleEvidenceAsync()
    {
        if (_lifecycleEvidence is not null)
            await _lifecycleEvidence.DisposeAsync();
    }
}
