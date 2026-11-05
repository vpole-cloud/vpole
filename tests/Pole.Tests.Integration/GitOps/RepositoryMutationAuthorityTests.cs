using Pole.Features.GitOps.Compiler;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Planning;
using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    [Fact]
    public async Task EveryMutationAuthorityComponentAndAcceptedOperationIsCheckedInsideItsTransaction()
    {
        await Apply(files:
        [
            new("group.yaml",
                """
                apiVersion: pole.io/v1alpha1
                kind: ResourceGroup
                metadata: {name: demo}
                spec: {description: original}
                """)
        ]);
        var original = await Pipeline.LatestAsync(source);
        var snapshot = Assert.IsType<GitInputAccepted>(original.Input).Snapshot;
        await SourceStore.RequestSyncAsync(new(TestData.TenantId,
                source,
                1,
                Guid.NewGuid().ToString(),
                new(GitRunTrigger.Manual, TestData.UserId)),
            default);
        var store = Services.GetRequiredService<GitRunStore>();
        var claim = Assert.IsType<GitWorkClaim>((await store.ClaimAsync(
            new(TestData.TenantId, source),
            "worker",
            default)).Value);
        Assert.True(await store.PinAsync(claim.Handle, snapshot.Commit, default));
        Assert.True(await store.AcceptAsync(claim.Handle, snapshot, default));
        var stamp = claim.Run.Stamp;
        var authority = new GitExecutionAuthority(stamp.ConfigurationRevision,
            stamp.Generation,
            claim.Handle.Identity.WorkerId,
            claim.Handle.Identity.Attempt,
            snapshot.Commit);
        var context = new GitMutationContext(TestData.TenantId,
            source,
            claim.Run.Id,
            stamp.Fence,
            claim.Source.DisplayUrl,
            authority);
        var accepted = snapshot.Resources.Single();
        var declaration = Services.GetRequiredService<ManifestCompiler>().Restore(snapshot.ApiVersion,
            [new(accepted.Kind, accepted.Name, accepted.SpecJson)]).Declarations.Single();
        var input = new GitOperationInput(context, declaration, DeclarationPhase.Prepare, accepted.ResourceId);
        var planned = await Services.GetRequiredService<GitOperationPlanning>().PlanAsync(input, default);
        var operation = Assert.IsType<GitOperationPlanned>(planned.Value).Operation;
        var invalid = new[]
        {
            input with { Context = context with { TenantId = Id() } },
            input with { Context = context with { SourceId = Id() } },
            input with { Context = context with { RunId = Id() } },
            input with { Context = context with { Fence = stamp.Fence + 1 } },
            input with { Context = context with { Authority = authority with { ConfigurationRevision = 2 } } },
            input with
            {
                Context = context with { Authority = authority with { Generation = stamp.Generation + 1 } }
            },
            input with { Context = context with { Authority = authority with { WorkerId = "other" } } },
            input with
            {
                Context = context with { Authority = authority with { Attempt = authority.Attempt + 1 } }
            },
            input with { Context = context with { Authority = authority with { Commit = new string('f', 40) } } },
            input with { ResourceId = Id() },
            input with { Phase = DeclarationPhase.Configure },
            input with { Declaration = declaration with { Hash = "different" } },
            input with { Declaration = declaration with { Address = new("Other", "demo") } }
        };
        var mutations = Services.GetRequiredService<IGitOpsMutations>();
        foreach (var value in invalid)
        {
            var called = false;
            var result = await mutations.ExecuteAsync(new(value, operation.Plan),
                (_, _) =>
                {
                    called = true;
                    return Task.FromResult(new GitOperationResult(value.ResourceId));
                },
                default);
            Assert.IsType<StaleGitExecution>(result.Failure);
            Assert.False(called);
        }

        var provider = Services.GetRequiredService<DeclarativeProviderRegistry>().Find("ResourceGroup")!;
        Assert.True((await provider.ApplyAsync(operation, default)).Applied);
        Assert.True(await store.CompleteAsync(claim.Handle, GitRunCompletion.ConfigurationApplied, [], default));
    }
}
