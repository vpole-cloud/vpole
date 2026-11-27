// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.GitOps.Compiler;
using Pole.Features.ResourceConnections.Domain;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected IMongoCollection<ResourceConnection> Connections =>
        Fixture.Database.GetCollection<ResourceConnection>("ResourceConnections");

    private protected static ManifestFile[] ConnectionSnapshot(string endpoint = "http") =>
    [
        .. StageFiveWebSnapshot(_ => { }),
        new(
            "connection.yaml",
            $$"""
              apiVersion: pole.io/v1alpha1
              kind: ResourceConnection
              metadata:
                name: api-loopback
              spec:
                source: { kind: WebService, name: demo-api }
                destination: { kind: WebService, name: demo-api }
                endpoint: {{endpoint}}
              """),
    ];
}
