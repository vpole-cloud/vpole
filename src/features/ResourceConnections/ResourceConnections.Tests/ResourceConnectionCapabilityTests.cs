using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.ResourceConnections.Application.Endpoints;
using Pole.Features.ResourceConnections.Application.Persistence;
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
            string tenantId,
            string resourceId,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            LastTenantId = tenantId;
            LastResourceId = resourceId;
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
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ResourceConnectionEndpointsFound>>(
                resourceIds.ToDictionary(id => id, id =>
                    new ResourceConnectionEndpointsFound(
                        endpoints ?? ImmutableList.Create(new ResourceConnectionEndpoint(
                            "queue", "queue-svc", NetworkPort.Create(5672),
                            NetworkPort.Create(5672))))));
    }

    public class NoopSessionProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException("The test only verifies session identity.");
    }
}
