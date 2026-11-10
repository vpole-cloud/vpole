using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class GitOperationPreviewTests
{
    private static readonly GitResourceIdentity Resource = new("Widget", "demo", "id");

    private static readonly GitResourcePreconditions Expected = new(
        Resource,
        "source",
        [new GitResourceVersion("Widget/id", 4)],
        "hash");

    private static readonly DateTime Now = new(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly GitRepositoryLocation Location = new("GitHub", "connection", "repository", "main", "");
    private static readonly GitAcceptedSnapshot Snapshot = new(new string('a', 40), "desired", "v1", [], []);

    private static readonly GitPreviewSourceStamp Source = new(
        new GitSourceIdentity("tenant", "source"),
        2,
        4,
        new GitPreviewRepositoryInput(Location, Snapshot));

    private static GitOperationPreview Preview() => GitOperationPreviewFactory.Create(
        "preview",
        Source,
        new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove),
        [Operation()],
        Now);

    private static GitPreviewConfirmationContext Context() => new(
        "preview",
        1,
        Source,
        [new GitPreviewObservation(Resource, Expected)],
        "reviewer",
        Now,
        "run");

    private static GitPreviewOperation Operation() => new(
        "remove",
        Resource,
        GitLifecycleAction.Remove,
        Expected,
        null,
        [],
        [],
        [],
        GitLifecycleProtection.ConfigurationOnly);

    [Theory]
    [InlineData(GitPreviewRemovalDisposition.Remove)]
    [InlineData(GitPreviewRemovalDisposition.Retain)]
    public void Removal_disposition_is_explicit_and_round_trips(GitPreviewRemovalDisposition removal)
    {
        var preview = GitOperationPreviewFactory.Create(
            "preview",
            Source,
            new GitPreviewResourceChanges(removal),
            [
                Operation() with
                {
                    Action = removal == GitPreviewRemovalDisposition.Retain
                        ? GitLifecycleAction.Retain
                        : GitLifecycleAction.Remove,
                },
            ],
            Now);
        var restored = BsonSerializer.Deserialize<GitOperationPreview>(preview.ToBsonDocument());
        Assert.Equal(removal, Assert.IsType<GitPreviewResourceChanges>(restored.Intent).Removal);
        RoundTrip(preview.Confirm(Context()));
    }

    [Fact]
    public void Unknown_removal_disposition_is_rejected_by_the_factory()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GitOperationPreviewFactory.Create(
            "invalid",
            Source,
            new GitPreviewResourceChanges((GitPreviewRemovalDisposition)99),
            [Operation()],
            Now));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("source")]
    [InlineData("configuration")]
    [InlineData("inventory")]
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
            "tenant" => context with { Source = Source with { Source = new GitSourceIdentity("other", "source") } },
            "source" => context with { Source = Source with { Source = new GitSourceIdentity("tenant", "other") } },
            "configuration" => context with { Source = Source with { ConfigurationRevision = 3 } },
            "inventory" => context with { Source = Source with { InventoryRevision = 5 } },
            "commit" => context with
            {
                Source = Source with
                {
                    Input = new GitPreviewRepositoryInput(Location, Snapshot with { Commit = new string('b', 40) }),
                },
            },
            "desired" => context with
            {
                Source = Source with
                {
                    Input = new GitPreviewRepositoryInput(Location, Snapshot with { DesiredHash = "new" }),
                },
            },
            "preview" => context with { PreviewId = "other" },
            "revision" => context with { PreviewRevision = 2 },
            "owner" => context with
            {
                Observations = [new GitPreviewObservation(Resource, Expected with { OwnerSourceId = "other" })],
            },
            "resource" => context with
            {
                Observations = [new GitPreviewObservation(Resource with { ResourceId = "recreated" }, Expected)],
            },
            "resource-version" => context with
            {
                Observations =
                [
                    new GitPreviewObservation(
                        Resource,
                        Expected with
                        {
                            Versions = [new GitResourceVersion("Widget/id", 5)],
                        }),
                ],
            },
            "configuration-hash" => context with
            {
                Observations =
                [
                    new GitPreviewObservation(
                        Resource,
                        Expected with
                        {
                            ConfigurationHash = "drift",
                        }),
                ],
            },
            "deleted" => context with { Observations = [new GitPreviewObservation(Resource, null)] },
            "duplicate" => context with
            {
                Observations =
                [new GitPreviewObservation(Resource, Expected), new GitPreviewObservation(Resource, Expected)],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.Equal(GitPreviewValidity.Stale, Preview().Evaluate(context));
        Assert.Throws<InvalidOperationException>(() => Preview().Confirm(context));
    }

    [Fact]
    public void Create_preview_requires_continued_absence()
    {
        var preview = GitOperationPreviewFactory.Create(
            "preview",
            Source,
            new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove),
            [Operation() with { Action = GitLifecycleAction.Apply, Expected = null }],
            Now);
        Assert.Equal(GitPreviewValidity.Stale, preview.Evaluate(Context()));
        Assert.Equal(
            GitPreviewValidity.Current,
            preview.Evaluate(
                Context() with
                {
                    Observations = [new GitPreviewObservation(Resource, null)],
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
            "version" => Expected with { Versions = [new GitResourceVersion("Widget/id", 5)] },
            "owner" => Expected with { OwnerSourceId = "other-source" },
            "hash" => Expected with { ConfigurationHash = "changed" },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.Equal(
            GitPreviewValidity.Stale,
            confirmed.Evaluate(
                Context() with
                {
                    PreviewRevision = confirmed.Revision,
                    Observations = [new GitPreviewObservation(Resource, current)],
                }));
    }

    [Fact]
    public void Detach_uses_inventory_without_any_repository_or_commit()
    {
        var source = Source with { Input = new GitPreviewInventoryInput() };
        var preview = GitOperationPreviewFactory.Create("offline", source, new GitPreviewDetachSource(), [], Now);
        var context = new GitPreviewConfirmationContext("offline", 1, source, [], "reviewer", Now, "run");
        Assert.Equal(GitPreviewValidity.Current, preview.Evaluate(context));
        RoundTrip(preview.Confirm(context));
    }

    [Fact]
    public void Other_actions_cannot_use_inventory_only_input()
    {
        Assert.Throws<ArgumentException>(() => GitOperationPreviewFactory.Create(
            "invalid",
            Source with { Input = new GitPreviewInventoryInput() },
            new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove),
            [Operation()],
            Now));
    }

    [Fact]
    public void Source_stamp_matches_current_schema_after_a_separate_Bson_read()
    {
        var preview = Preview();
        var reread = BsonSerializer.Deserialize<GitOperationPreview>(preview.ToBsonDocument());
        Assert.Equal(GitPreviewValidity.Current, preview.Evaluate(Context() with { Source = reread.Source }));
    }

    [Fact]
    public void Graph_rejects_cycles_and_dependencies_outside_the_reviewed_set()
    {
        Assert.Throws<ArgumentException>(() => GitOperationPreviewFactory.Create(
            "cycle",
            Source,
            new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove),
            [Operation() with { Dependencies = ["remove"] }],
            Now));
        Assert.Throws<ArgumentException>(() => GitOperationPreviewFactory.Create(
            "missing",
            Source,
            new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove),
            [Operation() with { Dependencies = ["unreviewed"] }],
            Now));
    }

    [Fact]
    public void Replacement_requires_a_new_identity_of_the_same_kind()
    {
        var selection = new GitReplacementSelection(Resource, "Widget", "renamed");
        var intent = new GitPreviewReplacement([selection]);
        var operation = Operation() with { Action = GitLifecycleAction.Replace };
        Assert.Throws<ArgumentException>(() =>
            GitOperationPreviewFactory.Create("missing", Source, intent, [operation], Now));
        Assert.Throws<ArgumentException>(() => GitOperationPreviewFactory.Create(
            "same",
            Source,
            intent,
            [operation with { Replacement = Resource }],
            Now));
        RoundTrip(
            GitOperationPreviewFactory.Create(
                "valid",
                Source,
                intent,
                [operation with { Replacement = new GitResourceIdentity("Widget", "renamed", "new-id") }],
                Now));
    }

    [Fact]
    public void Confirmation_retains_the_single_durable_run_identity()
    {
        var preview = Preview().Confirm(Context());
        Assert.Equal("run", Assert.IsType<GitPreviewConfirmed>(preview.State).RunId);
    }

    private static void RoundTrip(GitOperationPreview preview)
    {
        var document = preview.ToBsonDocument();
        Assert.Equal(document, BsonSerializer.Deserialize<GitOperationPreview>(document).ToBsonDocument());
    }
}
