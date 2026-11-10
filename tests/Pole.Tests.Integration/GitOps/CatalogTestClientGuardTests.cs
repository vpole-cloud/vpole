using Microsoft.Extensions.DependencyInjection;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed class CatalogTestClientGuardTests
{
    [Fact]
    public async Task Isolated_api_fixture_rejects_every_external_client_lookup()
    {
        var services = new ServiceCollection();
        NamedCatalogTestData.ConfigureIsolatedApi(services);
        using var provider = services.BuildServiceProvider();
        var clients = provider.GetRequiredService<IKubernetesClientProvider>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => clients.GetAsync(TestData.DefaultDeploymentTargetId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            clients.GetForCleanupAsync(TestData.DefaultDeploymentTargetId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => clients.GetDefaultAsync());
    }
}
