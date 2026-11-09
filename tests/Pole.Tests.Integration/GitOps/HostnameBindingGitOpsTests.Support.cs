using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.Domains.Migrations;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.WebServices.Application.Endpoints.Deployments.Hostnames;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected IMongoCollection<HostnameBinding> DomainBindings =>
        Fixture.Database.GetCollection<HostnameBinding>("HostnameBindings");

    private protected static ManifestFile[] HostnameSnapshot(string relativeName = "api") =>
    [
        .. DomainZoneSnapshot(),
        new("hostname.yaml", $$"""
            apiVersion: pole.io/v1alpha1
            kind: HostnameBinding
            metadata:
              name: api-hostname
            spec:
              zone: company-zone
              resource: {kind: WebService, name: demo-api}
              deploymentTarget: {{NamedCatalogTestData.TargetName}}
              relativeName: {{relativeName}}
            """)
    ];
}
