using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Migrations;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected static ManifestFile[] IsolationSnapshot(string group = "scope-group") =>
    [
        new("scope.yaml", $$"""
            apiVersion: pole.io/v1alpha1
            kind: IsolationScope
            metadata:
              name: tenant-private
            spec:
              resourceGroup: {{group}}
            ---
            apiVersion: pole.io/v1alpha1
            kind: ResourceGroup
            metadata:
              name: {{group}}
            spec: {}
            """)
    ];
}
