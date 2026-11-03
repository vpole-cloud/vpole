using MongoDB.Driver;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.Outbox.Contracts;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Jobs;

internal sealed class SourceBuildPromotion(
    IMongoCollection<SourceBuildConfiguration> configurations,
    IMongoCollection<SourceBuildJob> jobs,
    IMongoCollection<SourceBuildPromotionReceipt> receipts,
    StoreOutboxWrites outbox,
    FenceSourceRepository fenceRepository,
    TimeProvider timeProvider)
{
    public async Task<BeginSourceBuildPromotionOutcome> BeginAsync(
        SourceBuildPromotionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var previous = await receipts.Find(session, SourceBuildPromotionReceiptFilters.ByJob(request))
            .FirstOrDefaultAsync(cancellationToken);
        if (previous is not null)
            return previous.WebServiceId == request.WebServiceId &&
                previous.Artifact.Reference == request.Input.Artifact.Reference &&
                previous.Artifact.Digest == request.Input.Artifact.Digest &&
                previous.ConfigurationId == request.Authority.ConfigurationId &&
                previous.ConfigurationRevision == request.Authority.ConfigurationRevision &&
                previous.Generation == request.Authority.Generation &&
                previous.SourceRepositoryId == request.Input.SourceRepositoryId &&
                previous.CommitSha == request.Input.CommitSha
                ? new SourceBuildAlreadyPromoted(previous.ReleaseId, previous.WebServiceRevision)
                : new SourceBuildPromotionSuperseded();

        var job = await jobs.Find(session, SourceBuildJobFilters.ById(request.TenantId, request.BuildJobId))
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null || job.State is not PushingSourceBuild pushing ||
            job.SourceRepositoryId != request.Input.SourceRepositoryId ||
            job.CommitSha != request.Input.CommitSha ||
            pushing.Artifact.Reference != request.Input.Artifact.Reference ||
            pushing.Artifact.Digest != request.Input.Artifact.Digest ||
            job.WebServiceId != request.WebServiceId ||
            job.ConfigurationId != request.Authority.ConfigurationId ||
            job.InputVersion.ConfigurationRevision != request.Authority.ConfigurationRevision ||
            job.InputVersion.Generation != request.Authority.Generation ||
            job.Execution is not ClaimedSourceBuildExecution execution ||
            execution.WorkerId != request.Authority.WorkerId || execution.Attempt != request.Authority.Attempt)
            return new SourceBuildPromotionSuperseded();

        if (await fenceRepository(new(job.TenantId, job.SourceRepositoryId, job.InputVersion.RepositoryRevision),
            session, cancellationToken) is not SourceRepositoryFenced)
            return new SourceBuildPromotionSuperseded();

        var result = await configurations.UpdateOneAsync(session, SourceBuildConfigurationFilters.ForPromotion(job),
            Builders<SourceBuildConfiguration>.Update.Inc(item => item.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        if (result.ModifiedCount != 1)
            return new SourceBuildPromotionSuperseded();

        var fenced = await jobs.UpdateOneAsync(session,
            SourceBuildJobFilters.AtUpdatedAt(job) & SourceBuildJobFilters.LiveLease(job),
            Builders<SourceBuildJob>.Update.Inc(item => item.PromotionFence, 1),
            cancellationToken: cancellationToken);
        return fenced.ModifiedCount == 1 ? new SourceBuildPromotionAuthorized() : new SourceBuildPromotionSuperseded();
    }

    public async Task CompleteAsync(
        CompleteSourceBuildPromotionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var promotion = request.Promotion;
        var job = await jobs.Find(session, SourceBuildJobFilters.ById(promotion.TenantId, promotion.BuildJobId))
            .SingleAsync(cancellationToken);
        var artifact = SourceBuildArtifactFactory.Create(promotion.Input.Artifact.Reference,
            promotion.Input.Artifact.Digest);
        var receipt = SourceBuildPromotionReceiptFactory.Create(job, artifact,
            request.ReleaseId, request.Revision);
        await receipts.InsertOneAsync(session, receipt, cancellationToken: cancellationToken);
        var deploying = job.StartDeploying(artifact, request.ReleaseId,
            timeProvider.GetUtcNow().UtcDateTime);
        var written = await jobs.ReplaceOneAsync(session, SourceBuildJobFilters.AtUpdatedAt(job), deploying,
            cancellationToken: cancellationToken);
        if (written.ModifiedCount != 1)
            throw new InvalidOperationException("A fenced promotion must retain its build job.");

        await outbox(session, [OutboxWrite.Create(deploying.ToStateChangedEvent())], cancellationToken);
    }

}
