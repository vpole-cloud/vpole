using MongoDB.Driver;

namespace Pole.Features.GitProviders.Contracts;

public sealed record NamedSourceRepositoryRequest(
    string TenantId,
    string TechnicalName);

public sealed record NamedSourceRepositoryFound(SourceRepositoryBuildSnapshot Repository);

public sealed record NamedSourceRepositoryNotFound(string TechnicalName);

public union FindNamedSourceRepositoryOutcome(
    NamedSourceRepositoryFound,
    NamedSourceRepositoryNotFound);

public delegate Task<FindNamedSourceRepositoryOutcome> FindNamedSourceRepository(
    NamedSourceRepositoryRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

public sealed record SourceRepositoryReferenceRequest(
    string TenantId,
    string SourceRepositoryId);

public delegate Task<FindSourceRepositoryBuildOutcome> LoadSourceRepositoryReference(
    SourceRepositoryReferenceRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

public sealed record FenceSourceRepositoryRequest(
    string TenantId,
    string SourceRepositoryId,
    long ExpectedRevision);

public sealed record SourceRepositoryFenced;

public sealed record SourceRepositoryReferenceConflict(string SourceRepositoryId);

public union FenceSourceRepositoryOutcome(
    SourceRepositoryFenced,
    SourceRepositoryReferenceConflict);

public delegate Task<FenceSourceRepositoryOutcome> FenceSourceRepository(
    FenceSourceRepositoryRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

public sealed record SourceRepositoryCommitResolved(
    SourceRepositoryBuildSnapshot Repository,
    string CommitSha);

public sealed record SourceRepositoryRemoteNotFound;

public sealed record SourceRepositoryBranchNotFound(string Branch);

public sealed record SourceRepositoryAccessDenied;

public sealed record SourceRepositoryUnavailable;

public union ResolveSourceRepositoryCommitOutcome(
    SourceRepositoryCommitResolved,
    SourceRepositoryBuildNotFound,
    SourceRepositoryBuildConnectionNotFound,
    SourceRepositoryRemoteNotFound,
    SourceRepositoryBranchNotFound,
    SourceRepositoryAccessDenied,
    SourceRepositoryUnavailable);

public delegate Task<ResolveSourceRepositoryCommitOutcome> ResolveSourceRepositoryCommit(
    SourceRepositoryReferenceRequest request,
    CancellationToken cancellationToken);

public union CheckSourceRepositoryBuildAccessOutcome(
    SourceRepositoryBuildSnapshot,
    SourceRepositoryBuildNotFound,
    SourceRepositoryBuildConnectionNotFound,
    SourceRepositoryRemoteNotFound,
    SourceRepositoryAccessDenied,
    SourceRepositoryUnavailable);

public delegate Task<CheckSourceRepositoryBuildAccessOutcome> CheckSourceRepositoryBuildAccess(
    SourceRepositoryReferenceRequest request,
    CancellationToken cancellationToken);
