// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Domains.Domain;
using Pole.Features.GitOps.Compiler;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected IMongoCollection<TenantDomainZone> DomainZones =>
        Fixture.Database.GetCollection<TenantDomainZone>("TenantDomainZones");

    private protected static ManifestFile[] DomainZoneSnapshot(string label = "company") =>
    [
        new(
            "zone.yaml",
            $$"""
              apiVersion: pole.io/v1alpha1
              kind: DomainZone
              metadata:
                name: company-zone
              spec:
                deploymentTarget: {{NamedCatalogTestData.TargetName}}
                label: {{label}}
              """),
    ];
}
