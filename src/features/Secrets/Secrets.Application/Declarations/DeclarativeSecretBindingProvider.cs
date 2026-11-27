// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Primitives;

namespace Pole.Features.Secrets.Application.Declarations;

public sealed record SecretBindingDeclaration
{
    public required string Secret { get; init; }

    public required ResourceAddress Consumer { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<SecretBindingUsage>))]
    public SecretBindingUsage Usage { get; init; } = SecretBindingUsage.Runtime;

    public Dictionary<string, string> KeyMappings { get; init; } = [];
}

internal sealed class DeclarativeSecretBindingProvider(
    SecretBindingConsumerCatalog consumers,
    SecretBindingPlanning planning,
    SecretBindingExecution execution,
    SecretBindingAdoption adoption,
    SecretBindingLifecycle lifecycle,
    SecretBindingCandidates candidates
) : ITransactionalDeclarativeResourceProvider, IDeclarativeAdoptionPreviewProvider,
    IDeclarativeResourceAdoptionProvider, IDeclarativeResourceLifecycleProvider, IDeclarativeAdoptionCandidateProvider
{
    public Task<DeclarationAdoptionCandidatePage> FindAdoptionCandidatesAsync(
        FindDeclarationAdoptionCandidatesRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => candidates.FindAdoptionCandidatesAsync(request, session, ct);

    public Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => adoption.PreviewAdoptionAsync(request, session, ct);

    public Task<AdoptLifecycleResourceOutcome> AdoptAsync(
        AdoptLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => adoption.AdoptAsync(request, session, ct);

    public Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => lifecycle.ReadLifecycleAsync(request, session, ct);

    public Task<RemoveLifecycleResourceOutcome> RemoveAsync(
        RemoveLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => lifecycle.RemoveAsync(request, session, ct);

    public string Kind => "SecretBinding";

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        SecretBindingDeclarationRejected rejected => rejected.Reason switch
        {
            SecretBindingDeclarationError.BindingPrerequisiteUnavailable => "BindingPrerequisiteUnavailable",
            SecretBindingDeclarationError.SecretUnavailable => "SecretUnavailable",
            SecretBindingDeclarationError.ImmutableBindingChange => "ImmutableBindingChange",
            SecretBindingDeclarationError.ExplicitAdoptionRequired => "ExplicitAdoptionRequired",
            SecretBindingDeclarationError.SecretKeyUnavailable => "SecretKeyUnavailable",
            SecretBindingDeclarationError.BindingScopeUnavailable => "BindingScopeUnavailable",
            SecretBindingDeclarationError.SecretBindingLimitReached => "SecretBindingLimitReached",
            SecretBindingDeclarationError.SecretKeysInUse => "SecretKeysInUse",
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        },
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure)),
    };

    public DeclarationFacts Facts(JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretBindingDeclaration>(spec);

        return new DeclarationFacts(
            null,
            [],
            [
                new DeclaredReference(new ResourceAddress("Secret", value.Secret), "spec.secret"),
                new DeclaredReference(value.Consumer, "spec.consumer"),
                .. value.KeyMappings.Keys.Select(key => new DeclaredReference(
                    new ResourceAddress("Secret", value.Secret),
                    "spec.keyMappings",
                    key)),
            ],
            ExclusiveRelations:
            [
                new DeclarationExclusiveRelation(
                    SecretDeclarationRules.ConsumerBindingRelation,
                    new ResourceAddress("Secret", value.Secret),
                    value.Consumer),
            ],
            AccessGrants:
            [
                new DeclarationKeyGrant(
                    value.Consumer,
                    new ResourceAddress("Secret", value.Secret),
                    value.KeyMappings.Keys.ToArray(),
                    value.KeyMappings.Count == 0),
            ]);
    }

    public JsonElement NormalizeAndValidate(JsonElement spec) => consumers.NormalizeAndValidate(spec);

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretBindingDeclaration>(spec);
        return
        [
            new DeclarationStep(
                DeclarationPhase.Bind,
                [
                    new DeclarationDependency(new ResourceAddress("Secret", value.Secret), DeclarationPhase.Prepare),
                    new DeclarationDependency(value.Consumer, DeclarationPhase.Prepare),
                ]),
        ];
    }

    public Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => planning.ResolveAsync(request, session, ct);

    public Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => planning.ReadCurrentAsync(request, session, ct);

    public Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => planning.ProjectDesiredAsync(request, session, ct);

    public Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => planning.FenceAsync(request, session, ct);

    public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct) =>
        execution.ApplyAsync(operation, ct);
}
