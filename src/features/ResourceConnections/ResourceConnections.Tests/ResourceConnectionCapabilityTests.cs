using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.ResourceConnections.Application.Common;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Endpoints;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Application.References;
using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.ResourceConnections.Tests;

public sealed class ResourceConnectionCapabilityTests
{
    private static readonly ResourceTypeId CustomType =
        ResourceTypeId.Create("example/queue");

    [Fact]
    public async Task ProviderOutsideBuiltInSet_IsAcceptedByCreateHandler()
    {
        var called = false;
        var catalog = new ResourceConnectionEndpointCatalog(
            [new ResourceConnectionSourceCapability(CustomType)],
            [new CustomDestinationCapability()]);
        var handler = new CreateResourceConnectionHandler(
            (command, _) =>
            {
                called = true;
                Assert.Equal(CustomType, command.SourceResourceType);
                Assert.Equal(CustomType, command.DestinationResourceType);
                return Task.FromResult<CreateResourceConnectionPersistenceOutcome>(
                    new ConnectionResourceMissing());
            },
            catalog);

        var result = await handler.HandleAsync(new CreateResourceConnectionRequest(
            new CreateResourceConnectionBody(
                "queue-link",
                CustomType.Value,
                "source-id",
                CustomType.Value,
                "destination-id",
                "queue"),
            "tenant-id"));

        Assert.True(called);
        Assert.IsType<ConnectionResourceMissing>(result.Value);
    }

    [Fact]
    public async Task CustomDestination_ResolvesUsingCallerSession()
    {
        var capability = new CustomDestinationCapability();
        var catalog = new ResourceConnectionEndpointCatalog([], [capability]);
        var session = DispatchProxy.Create<IClientSessionHandle, NoopSessionProxy>();

        var result = await catalog.ResolveAsync(
            "tenant-id", CustomType, "resource-id", "queue", session,
            CancellationToken.None);

        var resolved = Assert.IsType<ResolvedConnectionEndpoint>(result.Value);
        Assert.Equal("queue-svc", resolved.ServiceName);
        Assert.Equal(5672, resolved.ServicePort.Value);
        Assert.Equal(5672, resolved.TargetPort.Value);
        Assert.Equal("tenant-id", capability.LastTenantId);
        Assert.Equal("resource-id", capability.LastResourceId);
        Assert.Same(session, capability.LastSession);
    }

    [Fact]
    public async Task DuplicatePublishedEndpointIds_AreRejectedClearly()
    {
        var capability = new CustomDestinationCapability(
            ImmutableList.Create(
                new ResourceConnectionEndpoint("queue", "first-svc",
                    NetworkPort.Create(5672), NetworkPort.Create(5672)),
                new ResourceConnectionEndpoint("queue", "second-svc",
                    NetworkPort.Create(5673), NetworkPort.Create(5673))));
        var catalog = new ResourceConnectionEndpointCatalog([], [capability]);
        var session = DispatchProxy.Create<IClientSessionHandle, NoopSessionProxy>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.ResolveAsync("tenant-id", CustomType, "resource-id", "queue", session,
                CancellationToken.None));
        Assert.Contains("duplicate connection endpoint IDs", exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdvisoryDiscovery_UsesCustomProviderMetadataWithoutProviderSwitches()
    {
        var endpoint = new ResourceConnectionEndpoint("queue", "queue-svc", NetworkPort.Create(5672), NetworkPort.Create(5672))
        {
            DisplayName = "Application queue",
            Protocol = "amqp",
            Configuration = [new("virtualHost", "Virtual host", "/app")]
        };
        var provider = new CustomDestinationCapability(ImmutableList.Create(endpoint));
        var catalog = new ResourceConnectionEndpointCatalog([], [provider]);
        var found = await catalog.LoadAvailableAsync("tenant", CustomType, "resource");
        Assert.Equal(endpoint, Assert.Single(found!.Endpoints));
        Assert.Equal("tenant", provider.LastTenantId);
        Assert.Equal("resource", provider.LastResourceId);
        Assert.Null(provider.LastSession);
        Assert.Null(await catalog.LoadAvailableAsync("tenant", ResourceTypeId.Create("example/unsupported"), "resource"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidMetadata_IsRejectedByDiscoveryAndAdmission(bool advisory)
    {
        var catalog = new ResourceConnectionEndpointCatalog([], [new CustomDestinationCapability(ImmutableList.Create(
            new ResourceConnectionEndpoint("queue", "svc", NetworkPort.Create(1), NetworkPort.Create(1)) { DisplayName = "" }))]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (advisory) await catalog.LoadAvailableAsync("tenant", CustomType, "resource");
            else await catalog.ResolveAsync("tenant", CustomType, "resource", "queue", DispatchProxy.Create<IClientSessionHandle, NoopSessionProxy>(), CancellationToken.None);
        });
    }

    [Theory]
    [InlineData("", "a", "SourceHasNoPlacements")]
    [InlineData("a", "a", "Eligible")]
    [InlineData("a,a", "a", "Eligible")]
    [InlineData("a,b", "a", "DestinationTargetMissing")]
    [InlineData("a", "b", "DestinationTargetMissing")]
    [InlineData("a", "a,a", "DestinationTargetAmbiguous")]
    public void SharedTopology_RequiresExactlyOneDestinationOnEachSourceTarget(string source, string destination, string kind) =>
        Assert.Equal(kind, ResourceConnectionTopology.Evaluate(source.Split(',', StringSplitOptions.RemoveEmptyEntries), destination.Split(',', StringSplitOptions.RemoveEmptyEntries)).Kind.ToString());

    [Fact]
    public async Task NoProviders_AllowsHostStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddResourceConnectionsFeature();
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task InvalidSourceRegistration_RejectsHostStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddResourceConnectionsFeature();
        builder.Services.AddSingleton(new ResourceConnectionSourceCapability(default));
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync());
        Assert.Contains("source type is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateSourceRegistration_RejectsHostStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddResourceConnectionsFeature();
        builder.Services.AddSingleton(new ResourceConnectionSourceCapability(CustomType));
        builder.Services.AddSingleton(new ResourceConnectionSourceCapability(CustomType));
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync());
        Assert.Contains(CustomType.Value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateDestinationRegistration_RejectsHostStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddResourceConnectionsFeature();
        builder.Services.AddScoped<IResourceConnectionDestinationCapability,
            CustomDestinationCapability>();
        builder.Services.AddScoped<IResourceConnectionDestinationCapability,
            CustomDestinationCapability>();
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync());
        Assert.Contains(CustomType.Value, exception.Message, StringComparison.Ordinal);
    }

    private sealed class CustomDestinationCapability(
        ImmutableList<ResourceConnectionEndpoint>? endpoints = null)
        : IResourceConnectionDestinationCapability
    {
        public ResourceTypeId ResourceType => CustomType;
        public string? LastTenantId { get; private set; }
        public string? LastResourceId { get; private set; }
        public IClientSessionHandle? LastSession { get; private set; }

        public Task<LoadResourceConnectionEndpointsOutcome> LoadAsync(
            LoadResourceConnectionDestinationRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            LastTenantId = request.TenantId;
            LastResourceId = request.ResourceId;
            LastSession = session;
            return Task.FromResult<LoadResourceConnectionEndpointsOutcome>(
                new ResourceConnectionEndpointsFound(
                    endpoints ?? ImmutableList.Create(new ResourceConnectionEndpoint(
                        "queue", "queue-svc",
                        NetworkPort.Create(5672), NetworkPort.Create(5672)))));
        }

        public Task<IReadOnlyDictionary<string, ResourceConnectionEndpointsFound>> LoadAvailableAsync(
            string tenantId,
            IReadOnlyCollection<string> resourceIds,
            CancellationToken cancellationToken = default)
        {
            LastTenantId = tenantId;
            LastResourceId = resourceIds.Single();
            return Task.FromResult<IReadOnlyDictionary<string, ResourceConnectionEndpointsFound>>(
                resourceIds.ToDictionary(id => id, id =>
                    new ResourceConnectionEndpointsFound(
                        endpoints ?? ImmutableList.Create(new ResourceConnectionEndpoint(
                            "queue", "queue-svc", NetworkPort.Create(5672),
                            NetworkPort.Create(5672))))));
        }
    }

    public class NoopSessionProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException("The test only verifies session identity.");
    }
}
