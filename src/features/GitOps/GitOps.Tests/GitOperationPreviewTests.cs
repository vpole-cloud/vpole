using Pole.Features.GitOps.Primitives;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.GitOps.Domain;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class GitOperationPreviewTests
{
    private static readonly GitResourceIdentity Resource = new("Widget", "demo", "id");
    private static readonly GitResourcePreconditions Expected = new(Resource, "source", [new("Widget/id", 4)], "hash");
    private static readonly GitPreviewSourceStamp Source = new(new("tenant", "source"), 2, "commit", "desired");

    private static GitOperationPreview Preview() => GitOperationPreviewFactory.Create(
        "preview", Source, new GitPreviewResourceChanges(),
        [new(Resource, GitPlannedAction.Remove, DeclarationPhase.Configure, Expected)]);

    private static GitPreviewConfirmationContext Context() => new("preview", 1, Source,
        [new(Resource, Expected)], "reviewer", new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc));

    [Theory]
    [InlineData("tenant")]
    [InlineData("source")]
    [InlineData("configuration")]
    [InlineData("commit")]
    [InlineData("desired")]
    [InlineData("preview")]
    [InlineData("revision")]
    [InlineData("owner")]
    [InlineData("resource")]
    [InlineData("resource-version")]
    [InlineData("configuration-hash")]
    [InlineData("deleted")]
    [InlineData("duplicate")]
    public void Any_changed_precondition_invalidates_confirmation(string change)
    {
        var context = Context();
        context = change switch
        {
            "tenant" => context with { Source = Source with { Source = new("other", "source") } },
            "source" => context with { Source = Source with { Source = new("tenant", "other") } },
            "configuration" => context with { Source = Source with { ConfigurationRevision = 3 } },
            "commit" => context with { Source = Source with { Commit = "new" } },
            "desired" => context with { Source = Source with { DesiredHash = "new" } },
            "preview" => context with { PreviewId = "other" },
            "revision" => context with { PreviewRevision = 2 },
            "owner" => context with { Observations = [new(Resource, Expected with { OwnerSourceId = "other" })] },
            "resource" => context with { Observations = [new(Resource with { ResourceId = "recreated" }, Expected)] },
            "resource-version" => context with
            {
                Observations = [new(Resource, Expected with
            {
                Versions = [new("Widget/id", 5)]
            })]
            },
            "configuration-hash" => context with
            {
                Observations = [new(Resource, Expected with
            {
                ConfigurationHash = "drift"
            })]
            },
            "deleted" => context with { Observations = [new(Resource, null)] },
            "duplicate" => context with { Observations = [new(Resource, Expected), new(Resource, Expected)] },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        Assert.Equal(GitPreviewValidity.Stale, Preview().Evaluate(context));
        Assert.Throws<InvalidOperationException>(() => Preview().Confirm(context));
    }

    [Fact]
    public void Create_preview_requires_continued_absence()
    {
        var preview = GitOperationPreviewFactory.Create("preview", Source, new GitPreviewResourceChanges(),
            [new(Resource, GitPlannedAction.Create, DeclarationPhase.Prepare, null)]);
        Assert.Equal(GitPreviewValidity.Stale, preview.Evaluate(Context()));
        Assert.Equal(GitPreviewValidity.Current, preview.Evaluate(Context() with
        {
            Observations = [new(Resource, null)]
        }));
    }

    [Fact]
    public void Confirmation_is_single_use_and_current_schema_round_trips()
    {
        var preview = Preview();
        RoundTrip(preview);
        var confirmed = preview.Confirm(Context());
        Assert.Equal(2, confirmed.Revision);
        Assert.IsType<GitPreviewConfirmed>(confirmed.State);
        Assert.Equal(GitPreviewValidity.AlreadyConfirmed, confirmed.Evaluate(Context() with { PreviewRevision = 2 }));
        RoundTrip(confirmed);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("owner")]
    [InlineData("hash")]
    public void Confirmed_preview_is_invalidated_by_later_resource_changes(string change)
    {
        var confirmed = Preview().Confirm(Context());
        var current = change switch
        {
            "version" => Expected with { Versions = [new("Widget/id", 5)] },
            "owner" => Expected with { OwnerSourceId = "other-source" },
            "hash" => Expected with { ConfigurationHash = "changed" },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        Assert.Equal(GitPreviewValidity.Stale, confirmed.Evaluate(Context() with
        {
            PreviewRevision = confirmed.Revision,
            Observations = [new(Resource, current)]
        }));
    }

    private static void RoundTrip(GitOperationPreview preview)
    {
        var document = preview.ToBsonDocument();
        Assert.Equal(document, BsonSerializer.Deserialize<GitOperationPreview>(document).ToBsonDocument());
    }
}
