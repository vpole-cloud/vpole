using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.SourceBuilds.Domain;

public enum SourceBuildRecipeKind
{
    Dockerfile,
    DotNet,
    Node
}

public sealed record SourceBuildRecipe(
    string CandidateKey,
    [property: BsonRepresentation(BsonType.String)]
    SourceBuildRecipeKind Kind,
    string SourceRoot,
    string ManifestPath,
    ImmutableList<string> Commands,
    string? StartCommand,
    string Dockerfile,
    bool IsCustomized);

public sealed record SourceBuildConfiguration(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string SourceRepositoryId,
    string WebServiceId,
    SourceBuildRecipe Recipe,
    string CreatedByUserId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public static class SourceBuildConfigurationFactory
{
    public sealed record CreateOptions(string Id, DateTime CreatedAtUtc);

    public static CreateOptions DefaultOptions =>
        new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow);

    public static SourceBuildConfiguration Create(
        string tenantId,
        string sourceRepositoryId,
        string webServiceId,
        SourceBuildRecipe recipe,
        string createdByUserId,
        CreateOptions options) =>
        new(
            options.Id,
            tenantId,
            sourceRepositoryId,
            webServiceId,
            recipe,
            createdByUserId,
            options.CreatedAtUtc,
            options.CreatedAtUtc);
}

public sealed record SourceBuildJob(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string ConfigurationId,
    string SourceRepositoryId,
    string WebServiceId,
    string Branch,
    string CommitSha,
    SourceBuildTrigger Trigger,
    SourceBuildJobState State,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(ManualSourceBuildTrigger))]
public abstract record SourceBuildTrigger;

public sealed record ManualSourceBuildTrigger(string UserId, string? Email) : SourceBuildTrigger;

public enum SourceBuildPhase
{
    Queued,
    Cloning,
    Analyzing,
    Building,
    Pushing,
    Deploying
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(QueuedSourceBuild),
    typeof(CloningSourceBuild),
    typeof(AnalyzingSourceBuild),
    typeof(BuildingSourceBuild),
    typeof(PushingSourceBuild),
    typeof(DeployingSourceBuild),
    typeof(SucceededSourceBuild),
    typeof(FailedSourceBuild))]
public abstract record SourceBuildJobState;

public sealed record QueuedSourceBuild(DateTime QueuedAtUtc) : SourceBuildJobState;
public sealed record CloningSourceBuild(DateTime StartedAtUtc) : SourceBuildJobState;
public sealed record AnalyzingSourceBuild(DateTime StartedAtUtc) : SourceBuildJobState;
public sealed record BuildingSourceBuild(DateTime StartedAtUtc) : SourceBuildJobState;

public sealed record PushingSourceBuild(
    DateTime StartedAtUtc,
    string ImageReference) : SourceBuildJobState;

public sealed record DeployingSourceBuild(
    DateTime StartedAtUtc,
    string ImageReference,
    string ImageDigest,
    string ReleaseId) : SourceBuildJobState;

public sealed record SucceededSourceBuild(
    DateTime CompletedAtUtc,
    string ImageReference,
    string ImageDigest,
    string ReleaseId) : SourceBuildJobState;

public sealed record FailedSourceBuild(
    DateTime FailedAtUtc,
    [property: BsonRepresentation(BsonType.String)]
    SourceBuildPhase Phase,
    string Code,
    string Message) : SourceBuildJobState;

public static class SourceBuildJobFactory
{
    public sealed record CreateOptions(string Id, DateTime CreatedAtUtc);

    public static CreateOptions DefaultOptions =>
        new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow);

    public static SourceBuildJob CreateManual(
        string tenantId,
        string configurationId,
        string sourceRepositoryId,
        string webServiceId,
        string branch,
        string commitSha,
        string userId,
        string? email,
        CreateOptions options) =>
        new(
            options.Id,
            tenantId,
            configurationId,
            sourceRepositoryId,
            webServiceId,
            branch,
            commitSha,
            new ManualSourceBuildTrigger(userId, email),
            new QueuedSourceBuild(options.CreatedAtUtc),
            options.CreatedAtUtc,
            options.CreatedAtUtc);
}

public static class SourceBuildJobTransitions
{
    public static SourceBuildJob StartCloning(this SourceBuildJob job, DateTime occurredAtUtc) =>
        Transition<QueuedSourceBuild>(job, new CloningSourceBuild(occurredAtUtc), occurredAtUtc);

    public static SourceBuildJob StartAnalyzing(this SourceBuildJob job, DateTime occurredAtUtc) =>
        Transition<CloningSourceBuild>(job, new AnalyzingSourceBuild(occurredAtUtc), occurredAtUtc);

    public static SourceBuildJob StartBuilding(this SourceBuildJob job, DateTime occurredAtUtc) =>
        Transition<AnalyzingSourceBuild>(job, new BuildingSourceBuild(occurredAtUtc), occurredAtUtc);

    public static SourceBuildJob StartPushing(
        this SourceBuildJob job,
        string imageReference,
        DateTime occurredAtUtc) =>
        Transition<BuildingSourceBuild>(
            job,
            new PushingSourceBuild(occurredAtUtc, imageReference),
            occurredAtUtc);

    public static SourceBuildJob StartDeploying(
        this SourceBuildJob job,
        string imageDigest,
        string releaseId,
        DateTime occurredAtUtc)
    {
        if (job.State is not PushingSourceBuild pushing)
            return InvalidTransition(job, nameof(PushingSourceBuild));

        return job with
        {
            State = new DeployingSourceBuild(
                occurredAtUtc,
                pushing.ImageReference,
                imageDigest,
                releaseId),
            UpdatedAtUtc = occurredAtUtc
        };
    }

    public static SourceBuildJob Succeed(this SourceBuildJob job, DateTime occurredAtUtc)
    {
        if (job.State is not DeployingSourceBuild deploying)
            return InvalidTransition(job, nameof(DeployingSourceBuild));

        return job with
        {
            State = new SucceededSourceBuild(
                occurredAtUtc,
                deploying.ImageReference,
                deploying.ImageDigest,
                deploying.ReleaseId),
            UpdatedAtUtc = occurredAtUtc
        };
    }

    public static SourceBuildJob Fail(
        this SourceBuildJob job,
        SourceBuildPhase phase,
        string code,
        string message,
        DateTime occurredAtUtc)
    {
        if (job.State is SucceededSourceBuild or FailedSourceBuild)
            return InvalidTransition(job, "an active build state");

        return job with
        {
            State = new FailedSourceBuild(occurredAtUtc, phase, code, message),
            UpdatedAtUtc = occurredAtUtc
        };
    }

    private static SourceBuildJob Transition<TExpected>(
        SourceBuildJob job,
        SourceBuildJobState nextState,
        DateTime occurredAtUtc)
        where TExpected : SourceBuildJobState
    {
        if (job.State is not TExpected)
            return InvalidTransition(job, typeof(TExpected).Name);

        return job with { State = nextState, UpdatedAtUtc = occurredAtUtc };
    }

    private static SourceBuildJob InvalidTransition(SourceBuildJob job, string expectedState) =>
        throw new InvalidOperationException(
            $"Build job {job.Id} cannot transition from {job.State.GetType().Name}; expected {expectedState}.");
}
