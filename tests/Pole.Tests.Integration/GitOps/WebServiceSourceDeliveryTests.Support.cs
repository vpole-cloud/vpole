using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitProviders.Application;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Domain;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected static void ConfigureSourceDeliveryServices(IServiceCollection services)
    {
        services.AddGitProvidersFeature(new ConfigurationBuilder().Build());
        services.AddSingleton<ResolveProviderRepository>((request, _) =>
            Task.FromResult<ResolveProviderRepositoryOutcome>(
                new ResolvedProviderRepository(
                    new ProviderRepository(
                        request.RepositoryId,
                        "application",
                        "organization/application",
                        "https://github.test/organization/application",
                        true,
                        request.Branch,
                        null),
                    new string('a', 40))));
    }

    private protected static ManifestFile[] SourceDeliverySnapshot()
    {
        var documents
            = StageFiveWebSnapshot(spec =>
                    spec["image"] = new JsonObject { ["sourceBuildConfiguration"] = "application-build" })[0]
                .Content
                .Split("\n---\n")
                .Select(item => JsonNode.Parse(item)!)
                .ToList();
        documents.Add(
            JsonNode.Parse(
                """
                {"apiVersion":"pole.io/v1alpha1","kind":"SourceRepository","metadata":{"name":"application"},
                 "spec":{"connection":"production","repositoryId":"42","branch":"main"}}
                """)!);
        documents.Add(
            JsonNode.Parse(
                """
                {"apiVersion":"pole.io/v1alpha1","kind":"SourceBuildConfiguration",
                 "metadata":{"name":"application-build"},"spec":{"repository":"application",
                 "webService":"demo-api","recipe":{"dockerfile":"FROM scratch"}}}
                """)!);
        return
        [
            new ManifestFile(
                "source-delivery.yaml",
                string.Join("\n---\n", documents.Select(item => item.ToJsonString()))),
        ];
    }

    private protected Task SeedApplicationConnectionAsync() => DbContext.GitProviderConnections.InsertOneAsync(
        GitProviderConnectionFactory.Create(
            Data.TenantId,
            "production",
            GitProviderKind.GitHub,
            Data.TenantId,
            "account",
            "organization",
            GitProviderAccountType.Organization,
            GitProviderRepositorySelection.All,
            Data.UserId,
            GitProviderConnectionFactory.DefaultOptions));
}
