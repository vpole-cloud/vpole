using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceConnections.Application.Common;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Endpoints;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Application.References;
using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceConnections.Migrations;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected IMongoCollection<ResourceConnection> Connections =>
        Fixture.Database.GetCollection<ResourceConnection>("ResourceConnections");

    private protected static ManifestFile[] ConnectionSnapshot(string endpoint = "http") =>
    [
        .. StageFiveWebSnapshot(_ => { }),
        new("connection.yaml", $$"""
            apiVersion: pole.io/v1alpha1
            kind: ResourceConnection
            metadata:
              name: api-loopback
            spec:
              source: { kind: WebService, name: demo-api }
              destination: { kind: WebService, name: demo-api }
              endpoint: {{endpoint}}
            """)
    ];
}
