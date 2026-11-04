using System.Text.Json.Serialization;
using Pole.ASP.Helpers;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.GitOps.Application.Persistence;

namespace Pole.Features.GitOps.Application.Endpoints.Sources.Previews;

public sealed record PreviewResourceView(
    string Kind,
    string Name,
    string ResourceId);

public sealed record LifecyclePreviewActionView(
    string Id,
    PreviewResourceView Resource,
    [property: JsonConverter(typeof(JsonStringEnumConverter<GitLifecycleAction>))] GitLifecycleAction Action,
    PreviewResourceView? Replacement,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> ChangedFields,
    IReadOnlyList<string> Warnings);

public sealed record LifecyclePreviewActionPage(
    IReadOnlyList<LifecyclePreviewActionView> Items,
    int Page,
    int PageSize,
    bool HasMore,
    int Total);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LifecycleInventoryPreviewInputView), "Inventory")]
[JsonDerivedType(typeof(LifecycleRepositoryPreviewInputView), "Repository")]
public closed record LifecyclePreviewInputView;

public sealed record LifecycleInventoryPreviewInputView : LifecyclePreviewInputView;

public sealed record LifecyclePreviewLocationView(
    string Provider,
    string ConnectionId,
    string ExternalRepositoryId,
    string Branch,
    string ManifestDirectory);

public sealed record LifecycleRepositoryPreviewInputView(
    LifecyclePreviewLocationView Repository,
    string DesiredHash) : LifecyclePreviewInputView;

public sealed record LifecyclePreviewSummaryView(
    int Apply,
    int Adopt,
    int Remove,
    int Replace,
    int Retain,
    int Detach,
    int Total,
    IReadOnlyList<string> Warnings);

public sealed record LifecyclePreviewView(
    string Id,
    long Revision,
    long SourceRevision,
    long InventoryRevision,
    string Intent,
    string State,
    string? Commit,
    DateTime CreatedAtUtc,
    string? RunId,
    LifecyclePreviewActionPage Actions,
    LifecyclePreviewInputView Input,
    LifecyclePreviewSummaryView Summary);

public sealed record LifecyclePreviewPage(
    IReadOnlyList<LifecyclePreviewView> Items,
    int Page,
    int PageSize,
    bool HasMore);

internal static class LifecyclePreviewViews
{
    public static LifecyclePreviewView View(GitOperationPreview preview, int page = 1, int pageSize = 100) => new(
        preview.Id, preview.Revision, preview.Source.ConfigurationRevision, preview.Source.InventoryRevision,
        preview.Intent switch
        {
            GitPreviewAdoption => "Adoption",
            GitPreviewRemoval => "Removal",
            GitPreviewReplacement => "Replacement",
            GitPreviewResourceChanges => "ResourceChanges",
            GitPreviewRelocateSource => "Relocation",
            GitPreviewDetachSource => "Detach"
        }, preview.State switch
        {
            GitPreviewAwaitingConfirmation => "AwaitingConfirmation",
            GitPreviewConfirmed => "Confirmed",
            GitPreviewPolicyAuthorized => "PolicyAuthorized"
        },
        (preview.Source.Input as GitPreviewRepositoryInput)?.Snapshot.Commit, preview.CreatedAtUtc,
        preview.State switch
        {
            GitPreviewAwaitingConfirmation => null,
            GitPreviewConfirmed confirmed => confirmed.RunId,
            GitPreviewPolicyAuthorized policy => policy.RunId
        },
        new(preview.Operations.Skip((page - 1) * pageSize).Take(pageSize).Select(item => new LifecyclePreviewActionView(
            item.Id, Resource(item.Resource), item.Action, item.Replacement is { } next ? Resource(next) : null,
            item.Dependencies, item.ChangedFields, item.Warnings)).ToArray(), page, pageSize,
            (long)page * pageSize < preview.Operations.Length, preview.Operations.Length),
        Input(preview.Source.Input),
        new(preview.Operations.Count(item => item.Action == GitLifecycleAction.Apply),
            preview.Operations.Count(item => item.Action == GitLifecycleAction.Adopt),
            preview.Operations.Count(item => item.Action == GitLifecycleAction.Remove),
            preview.Operations.Count(item => item.Action == GitLifecycleAction.Replace),
            preview.Operations.Count(item => item.Action == GitLifecycleAction.Retain),
            preview.Operations.Count(item => item.Action == GitLifecycleAction.Detach), preview.Operations.Length,
            preview.Operations.SelectMany(item => item.Warnings).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray()));

    private static LifecyclePreviewInputView Input(GitPreviewInput input) => input switch
    {
        GitPreviewInventoryInput => new LifecycleInventoryPreviewInputView(),
        GitPreviewRepositoryInput repository => new LifecycleRepositoryPreviewInputView(new(
            repository.Repository.Provider, repository.Repository.ConnectionId,
            repository.Repository.ExternalRepositoryId, repository.Repository.Branch,
            repository.Repository.ManifestDirectory), repository.Snapshot.DesiredHash)
    };

    private static PreviewResourceView Resource(GitResourceIdentity identity) => new(identity.Kind, identity.Name,
        identity.ResourceId);
}

internal static class LifecycleHttpMappings
{
    public static HttpProblem Map(GitPreviewNotFound _) =>
        new(404, PoleProblemTypeId.FromCode("gitops.preview-not-found"), "Preview not found",
            "gitops.preview-not-found");

    public static HttpProblem Map(GitPreviewStale _) =>
        new(409, PoleProblemTypeId.FromCode("gitops.preview-stale"), "Preview changed; create a new preview",
            "gitops.preview-stale");

    public static HttpProblem Map(GitPreviewReadFailed value) =>
        new(409, PoleProblemTypeId.FromCode("gitops.preview-input-unavailable"), value.Code,
            "gitops.preview-input-unavailable");

    public static HttpProblem Map(GitPreviewRejected rejected) => rejected.Failure switch
    {
        GitLifecycleRetainPolicyRequired => new(409, PoleProblemTypeId.FromCode("gitops.retain-policy-required"),
            "Change the source prune policy to Retain before requesting retained removals",
            "gitops.retain-policy-required"),
        _ => new(409, PoleProblemTypeId.FromCode("gitops.preview-blocked"), "The requested lifecycle plan is blocked",
            "gitops.preview-blocked")
    };
}
