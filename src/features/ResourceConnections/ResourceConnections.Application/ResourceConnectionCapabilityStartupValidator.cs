using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pole.Features.ResourceConnections.Application.Topology;

namespace Pole.Features.ResourceConnections.Application;

internal sealed class ResourceConnectionCapabilityStartupValidator(
    IServiceScopeFactory scopes
) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<ResourceConnectionEndpointCatalog>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
