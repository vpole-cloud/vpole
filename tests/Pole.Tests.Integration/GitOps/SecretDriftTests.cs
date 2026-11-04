using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Sources.Mongo;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    private static ManifestFile[] SecretSnapshot() =>
    [
        .. GroupSnapshot(),
        new("secret.yaml", """
            apiVersion: pole.io/v1alpha1
            kind: Secret
            metadata:
              name: drift-secret
            spec:
              resourceGroup: drift-group
              purpose: managed purpose
              keys: [API_TOKEN]
            """)
    ];

    [Fact]
    public async Task SecretSameCommitRepairsMetadataAndRotationRemainsNoOpWithoutValueDisclosure()
    {
        var files = SecretSnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var secret = await Secrets.Find(item => item.Name == "drift-secret").SingleAsync();
        await Secrets.UpdateOneAsync(item => item.Id == secret.Id,
            Builders<Secret>.Update.Set(item => item.Purpose, "manual purpose").Inc(item => item.Revision, 1));
        var repaired = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, repaired.Status);
        Assert.Equal(secret.Purpose, (await Secrets.Find(item => item.Id == secret.Id).SingleAsync()).Purpose);
        var queries = Services.GetRequiredService<GitRepositoryQueries>();
        var operations = await queries.OperationsAsync(TestData.TenantId, source, repaired.Context!.RunId, 1, 20,
            default);
        var plan = operations!.Items.Single(item => item.Kind == "Secret").Plan!;
        Assert.Equal(GitPlannedAction.Update, plan.Action);
        Assert.Equal<string>(["configuration.purpose"], plan.ChangedFields);

        await Supply("rotation-value-must-stay-private");
        var rotated = await Secrets.Find(item => item.Id == secret.Id).SingleAsync();
        var events = await BusinessEvents();
        var unchanged = await Apply(files);
        var after = await Secrets.Find(item => item.Id == secret.Id).SingleAsync();
        Assert.Equal(rotated.Revision, after.Revision);
        Assert.Equal(rotated.UpdatedAtUtc, after.UpdatedAtUtc);
        Assert.Equal(rotated.CurrentVersion, after.CurrentVersion);
        Assert.Equal(events, await BusinessEvents());
        var run = await queries.RunAsync(TestData.TenantId, source, unchanged.Context!.RunId, default);
        Assert.Equal(RepositoryRunStatus.AlreadyCurrent, run!.Status);
        Assert.Equal(new RepositoryOperationSummary(0, 0, 2, 0, 0, null), run.Operations);
        var inventory = await Inventory.Find(item => item.ResourceId == secret.Id).SingleAsync();
        Assert.DoesNotContain("rotation-value", inventory.Projections.Applied!.ProjectionJson);
        Assert.DoesNotContain("currentVersion", inventory.Projections.Applied.ProjectionJson);
    }
}
