using Pole.Multitenancy.Primitives;
using Pole.Workloads;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.SourceBuilds.Domain;

public sealed record SourceBuildRecipe(string Dockerfile)
{
    public static SourceBuildRecipe Create(string dockerfile)
    {
        if (string.IsNullOrWhiteSpace(dockerfile) || dockerfile.Length > 100_000)
            throw new ArgumentException("A bounded executable Dockerfile is required.", nameof(dockerfile));

        return new(dockerfile);
    }
}

public sealed record SourceBuildArtifact(
    string Reference,
    string Digest);

public static class SourceBuildArtifactFactory
{
    public static SourceBuildArtifact Create(string reference, string digest)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new ArgumentException("A non-empty image reference is required.", nameof(reference));
        if (string.IsNullOrWhiteSpace(digest))
            throw new ArgumentException("A non-empty image digest is required.", nameof(digest));

        return new(reference, digest);
    }
}

public sealed record SourceBuildConfiguration(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string TechnicalName,
    string SourceRepositoryId,
    string WebServiceId,
    SourceBuildRecipe Recipe,
    long Revision,
    long ReferenceFence,
    long BuildGeneration,
    SourceBuildConfigurationOrigin Origin,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc) : ITenantOwned;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(ManualSourceBuildConfigurationOrigin), typeof(GitSourceBuildConfigurationOrigin))]
public closed record SourceBuildConfigurationOrigin;

public sealed record ManualSourceBuildConfigurationOrigin(string UserId) : SourceBuildConfigurationOrigin;

public sealed record GitSourceBuildConfigurationOrigin(
    string SourceId,
    string RunId) : SourceBuildConfigurationOrigin;

public static class SourceBuildConfigurationFactory
{
    public sealed record CreateOptions(
        string Id,
        DateTime CreatedAtUtc);

    public static CreateOptions DefaultOptions =>
        new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow);

    public static SourceBuildConfiguration Create(
        string tenantId,
        string technicalName,
        string sourceRepositoryId,
        string webServiceId,
        SourceBuildRecipe recipe,
        SourceBuildConfigurationOrigin origin,
        CreateOptions options)
    {
        if (!ResourceTechnicalName.IsValid(technicalName))
            throw new ArgumentException("A valid technical name is required.", nameof(technicalName));

        return new(
            options.Id,
            tenantId,
            technicalName,
            sourceRepositoryId,
            webServiceId,
            SourceBuildRecipe.Create(recipe.Dockerfile),
            1,
            0,
            0,
            origin,
            options.CreatedAtUtc,
            options.CreatedAtUtc);
    }
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
    SourceBuildInputVersion InputVersion,
    long PromotionFence,
    SourceBuildExecution Execution,
    SourceBuildRecipe Recipe,
    SourceBuildTrigger Trigger,
    SourceBuildJobState State,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc) : ITenantOwned;

public sealed record SourceBuildInputVersion(
    long ConfigurationRevision,
    long RepositoryRevision,
    long Generation);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(ManualSourceBuildTrigger))]
public abstract record SourceBuildTrigger;

public sealed record ManualSourceBuildTrigger(
    string UserId,
    string? Email) : SourceBuildTrigger;

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
    SourceBuildArtifact Artifact) : SourceBuildJobState;

public sealed record DeployingSourceBuild(
    DateTime StartedAtUtc,
    SourceBuildArtifact Artifact,
    string ReleaseId) : SourceBuildJobState;

public sealed record SucceededSourceBuild(
    DateTime CompletedAtUtc,
    SourceBuildArtifact Artifact,
    string ReleaseId) : SourceBuildJobState;

public sealed record FailedSourceBuild(
    DateTime FailedAtUtc,
    [property: BsonRepresentation(BsonType.String)]
    SourceBuildPhase Phase,
    string Code,
    string Message) : SourceBuildJobState;

public static class SourceBuildJobFactory
{
    public sealed record CreateOptions(
        string Id,
        DateTime CreatedAtUtc);

    public static CreateOptions DefaultOptions =>
        new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow);

    public static SourceBuildJob CreateManual(
        string tenantId,
        string configurationId,
        string sourceRepositoryId,
        string webServiceId,
        string branch,
        string commitSha,
        SourceBuildInputVersion inputVersion,
        SourceBuildRecipe recipe,
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
            inputVersion,
            0,
            new AvailableSourceBuildExecution(0),
            recipe,
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
        SourceBuildArtifact artifact,
        DateTime occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return Transition<BuildingSourceBuild>(
            job,
            new PushingSourceBuild(occurredAtUtc,
                SourceBuildArtifactFactory.Create(artifact.Reference, artifact.Digest)),
            occurredAtUtc);
    }

    public static SourceBuildJob StartDeploying(
        this SourceBuildJob job,
        SourceBuildArtifact artifact,
        string releaseId,
        DateTime occurredAtUtc)
    {
        if (job.State is not PushingSourceBuild pushing)
            return InvalidTransition(job, nameof(PushingSourceBuild));

        ArgumentNullException.ThrowIfNull(artifact);
        var promotedArtifact = SourceBuildArtifactFactory.Create(artifact.Reference, artifact.Digest);
        if (pushing.Artifact != promotedArtifact)
            throw new ArgumentException("Promotion must retain the built artifact.", nameof(artifact));

        return job with
        {
            State = new DeployingSourceBuild(
                occurredAtUtc,
                promotedArtifact,
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
                deploying.Artifact,
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
