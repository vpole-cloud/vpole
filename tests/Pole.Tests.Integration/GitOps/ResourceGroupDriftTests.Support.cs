using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Outbox.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected static ManifestFile[] GroupSnapshot() =>
    [
        new("group.yaml", """
            apiVersion: pole.io/v1alpha1
            kind: ResourceGroup
            metadata:
              name: drift-group
            spec:
              description: managed description
              tags: [production, api]
            """)
    ];

    private protected Task<long> BusinessEvents() => DbContext.OutboxMessages.CountDocumentsAsync(
        ScenarioFilter<OutboxMessage>(item => item.TenantId) & Builders<OutboxMessage>.Filter.Ne(item => item.EventType, "pole.gitops/v1/source-work-requested"));
}
