using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    [Fact]
    public async Task BindingSameCommitRepairsMappingWithoutChangingPinsOrCreatingRelease()
    {
        var snapshot = Snapshot(keys: ["UNUSED", "API_TOKEN"]);
        var initial = await Apply(snapshot);
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        var secret = await Secrets.Find(Builders<Secret>.Filter.Empty).SingleAsync();
        var binding = secret.Bindings.Single();
        await Secrets.ReplaceOneAsync(item => item.Id == secret.Id, secret with
        {
            Revision = secret.Revision + 1,
            Bindings = [binding with { KeyMappings = ImmutableDictionary<string, string>.Empty
                .Add("API_TOKEN", "API_TOKEN").Add("UNUSED", "RENAMED") }]
        });
        var releases = await Releases();
        var repaired = await Apply(snapshot);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, repaired.Status);
        var current = (await Secrets.Find(item => item.Id == secret.Id).SingleAsync()).Bindings.Single();
        Assert.Empty(current.KeyMappings);
        Assert.Equal(binding.Pins.Required.ToArray(), current.Pins.Required.ToArray());
        Assert.Equal(binding.Pins.Retained.ToArray(), current.Pins.Retained.ToArray());
        Assert.Equal(releases, await Releases());
        var queries = Services.GetRequiredService<GitRepositoryQueries>();
        var operations = await queries.OperationsAsync(TestData.TenantId, source, repaired.Context!.RunId, 1, 20,
            default);
        Assert.Equal(GitPlannedAction.Update,
            operations!.Items.Single(item => item.Kind == "SecretBinding").Plan!.Action);
        var events = await BusinessEvents();
        var stable = await Apply(snapshot);
        Assert.Equal(events, await BusinessEvents());
        var view = await queries.RunAsync(TestData.TenantId, source, stable.Context!.RunId, default);
        Assert.Equal(new RepositoryOperationSummary(0, 0, 5, 0, 0), view!.Operations);
    }
}
