using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Contracts;
using Xunit;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Tests;

public sealed class DeclarativeResourcePlannerTests
{
    [Theory]
    [InlineData("create", typeof(CreateDeclarationPlan))]
    [InlineData("same", typeof(NoOpDeclarationPlan))]
    [InlineData("update", typeof(UpdateDeclarationPlan))]
    [InlineData("prepared", typeof(UpdateDeclarationPlan))]
    [InlineData("replace", typeof(ReplacementDeclarationPlan))]
    [InlineData("adopt", typeof(AdoptionDeclarationPlan))]
    [InlineData("foreign", typeof(BlockedDeclarationPlan))]
    [InlineData("rebound", typeof(BlockedDeclarationPlan))]
    [InlineData("removed", typeof(BlockedDeclarationPlan))]
    [InlineData("blocked", typeof(BlockedDeclarationPlan))]
    public async Task Registered_provider_controls_projections_without_engine_kind_switch(
        string scenario,
        Type expected)
    {
        var provider = new WidgetProvider(scenario);
        var planner = new DeclarativeResourcePlanner(new([provider]), new Ownership(scenario));
        var result = await planner.PlanAsync(new(new("tenant",
                    "source",
                    scenario is "rebound" or "removed" ? "accepted-id" : null),
                new(new("ThirdPartyWidget", "demo"), Json("{}"), "hash", [], "test.yaml", 1)),
            null!,
            default);
        Assert.IsType(expected, result);
    }

    [Theory]
    [InlineData(GitResourceMaterialization.Allocated, typeof(CreateDeclarationPlan))]
    [InlineData(GitResourceMaterialization.Created, typeof(BlockedDeclarationPlan))]
    public async Task Accepted_identity_distinguishes_first_creation_from_missing_managed_resource(
        GitResourceMaterialization materialization, Type expected)
    {
        var declaration = new CompiledDeclaration(new("ThirdPartyWidget", "demo"), Json("{}"), "hash", [],
            "test.yaml", 1);
        var input = new GitOperationInput(new("tenant", "source", "run", 1, "https://example.test/repository",
            new(1, 1, "worker", 1, new string('a', 40))), declaration, DeclarationPhase.Prepare, "id");
        var planner = new DeclarativeResourcePlanner(new([new WidgetProvider("create")]), new Ownership("same"));
        var result = await planner.PlanAsync(new(new("tenant", "source", "id",
            new(input, "source", materialization)), declaration), null!, default);
        Assert.IsType(expected, result);
        if (result is BlockedDeclarationPlan blocked)
            Assert.IsType<DeclarationManagedResourceMissing>(blocked.Failure);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class WidgetProvider(string scenario) : IDeclarativeResourcePlanningProvider
    {
        public string Kind => "ThirdPartyWidget";

        public JsonElement NormalizeAndValidate(JsonElement spec) => spec;

        public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec) => [];

        public Task<string?> ResolveAsync(
            ResolveDeclaredResourceRequest request,
            IClientSessionHandle session,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
            ReadCurrentDeclarationRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken)
        {
            Assert.Equal("tenant", request.TenantId);
            return Task.FromResult<ReadCurrentDeclarationOutcome>(scenario is "create" or "removed"
                ? new CurrentDeclarationMissing()
                : new CurrentDeclaration("id",
                    new([new("Widget/id", 7)]),
                    new(Json("{}"), Json("{\"b\":2,\"a\":1}")),
                    scenario == "prepared"
                        ? DeclarationConfigurationState.Prepared
                        : DeclarationConfigurationState.Configured));
        }

        public Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
            ProjectDesiredDeclarationRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken) => Task.FromResult<ProjectDesiredDeclarationOutcome>(
            scenario == "blocked"
                ? new DeclarationReadBlocked(new DeclarationReferenceUnavailable(new("X", "y")))
                : new DeclarationProjection(Json(scenario == "replace" ? "{\"parent\":\"new\"}" : "{}"),
                    Json(scenario == "update" ? "{\"a\":3,\"b\":2}" : "{\"a\":1,\"b\":2}")));
    }

    private sealed class Ownership(string scenario) : IGitOpsMutations
    {
        public Task<GitResourceStatus?> StatusAsync(LoadGitResourceStatusRequest request, CancellationToken ct)
        {
            Assert.Equal("tenant", request.TenantId);
            return Task.FromResult<GitResourceStatus?>(scenario == "adopt"
                ? null
                : new(scenario == "foreign" ? "other" : "source", "url", null, false, true));
        }

        public Task<GitOperationResult> ExecuteAsync(GitOperation operation,
            Func<IClientSessionHandle, CancellationToken, Task<GitOperationResult>> mutation,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitOperationResult?> BeginAsync(
            GitOperation operation,
            IClientSessionHandle session,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitOperationResult> CompleteAsync(
            CompleteGitOperationRequest request,
            IClientSessionHandle session,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> AuthorizeAsync(
            AuthorizeGitResourceMutationRequest request,
            IClientSessionHandle session,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string?> ResolveAsync(
            ResolveGitResourceRequest request,
            IClientSessionHandle session,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string?> ResolveAcceptedAsync(
            ResolveAcceptedGitReferenceRequest request,
            IClientSessionHandle session,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PinGitReferenceOutcome> PinReferenceAsync(PinGitReferenceRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, GitResourceStatus>> StatusesAsync(
            LoadGitResourceStatusesRequest request,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
