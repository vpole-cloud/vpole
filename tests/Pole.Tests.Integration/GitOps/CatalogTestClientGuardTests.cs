using System.Net;
using Pole.Tests.Integration.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Pole.Features.ComputeProfiles.Migrations;
using Pole.Features.ComputeProfiles.Application;
using Pole.Features.Outbox.Domain;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Migrations;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.ComputeProfiles;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
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
