using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.Domains.Application.Persistence.HostnameBindings;
using Pole.Features.Domains.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.Domains.Tests;

public sealed class HostnameBindingDeclarationTests
{
    private static DeclarativeHostnameBindingProvider Provider() => new(
        new MongoClient("mongodb://localhost:27017").GetDatabase("domain-schema-tests"),
        null!, null!, null!, null!, null!, null!, null!, null!, [new WorkloadCapability()]);

    [Theory]
    [InlineData("@", "@")]
    [InlineData(" API.V2 ", "api.v2")]
    public void Hostname_normalizes_relative_dns_and_allows_cross_source_references(string relative, string expected)
    {
        var provider = Provider();
        var normalized = provider.NormalizeAndValidate(Spec(relative));
        Assert.Equal(expected, DeclarationJson.Read<HostnameBindingDeclaration>(normalized).RelativeName);
        var facts = provider.Facts(normalized);
        Assert.False(facts.ReferencesShareScope);
        Assert.Equal(new ResourceAddress("DomainZone", "company"), facts.References[0].Target);
        Assert.Equal(new ResourceAddress("WebService", "api"), facts.References[1].Target);
        var step = Assert.Single(provider.Steps(new("HostnameBinding", "api-hostname"), normalized));
        Assert.Equal(DeclarationPhase.Bind, step.Phase);
        Assert.Contains(new(new("DomainZone", "company"), DeclarationPhase.Prepare), step.Dependencies);
        Assert.Contains(new(new("WebService", "api"), DeclarationPhase.Configure), step.Dependencies);
    }

    [Theory]
    [InlineData("/root")]
    [InlineData("..")]
    [InlineData("*.example")]
    [InlineData("example.")]
    public void Hostname_rejects_invalid_dns_paths(string relative)
    {
        var exception = Assert.Throws<DeclarationSchemaException>(() =>
            Provider().NormalizeAndValidate(Spec(relative)));
        Assert.Equal("spec.relativeName", exception.Field);
    }

    [Theory]
    [InlineData("RedisService")]
    [InlineData("not-registered")]
    public void Hostname_requires_owner_registered_workload_capability(string kind)
    {
        var exception = Assert.Throws<DeclarationSchemaException>(() =>
            Provider().NormalizeAndValidate(Spec("api", kind)));
        Assert.Equal("spec.resource", exception.Field);
    }

    [Fact]
    public void Hostname_rejects_unknown_fields() =>
        Assert.Throws<JsonException>(() => Provider().NormalizeAndValidate(DeclarationJson.Write(new
        {
            zone = "company",
            resource = new { kind = "WebService", name = "api" },
            deploymentTarget = "public-target",
            relativeName = "api",
            hostname = "cannot-override.example"
        })));

    private static JsonElement Spec(string relative, string kind = "WebService") => DeclarationJson.Write(new
    {
        zone = "company",
        resource = new { kind, name = "api" },
        deploymentTarget = "public-target",
        relativeName = relative
    });

    private sealed class WorkloadCapability : IHostnameBindingWorkloadCapability
    {
        public string Kind => "WebService";
        public ResourceTypeId ResourceType => ResourceTypeId.Create("pole/web-service");

        public Task<ReadHostnameAttachmentOutcome> ReadAsync(ReadHostnameAttachmentRequest request,
            IClientSessionHandle session, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FenceHostnameAttachmentOutcome> FenceAsync(HostnameAttachmentSnapshot snapshot,
            IClientSessionHandle session, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ApplyHostnameAttachmentOutcome> ApplyAsync(ApplyHostnameAttachmentRequest request,
            IClientSessionHandle session, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
