using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Dns;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Application.Endpoints;
using Pole.Features.Domains.Domain;
using Pole.Features.Domains.Migrations;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected IMongoCollection<TenantDomainZone> DomainZones =>
        Fixture.Database.GetCollection<TenantDomainZone>("TenantDomainZones");

    private protected static ManifestFile[] DomainZoneSnapshot(string label = "company") =>
    [
        new("zone.yaml", $$"""
            apiVersion: pole.io/v1alpha1
            kind: DomainZone
            metadata:
              name: company-zone
            spec:
              deploymentTarget: {{NamedCatalogTestData.TargetName}}
              label: {{label}}
            """)
    ];
}
