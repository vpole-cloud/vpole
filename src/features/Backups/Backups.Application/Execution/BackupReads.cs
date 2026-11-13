using MongoDB.Driver;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;

namespace Pole.Features.Backups.Application.Execution;

public sealed class BackupReads(
    IMongoCollection<BackupDestination> destinations,
    IMongoCollection<BackupPolicy> policies,
    IMongoCollection<BackupOperation> operations,
    IMongoCollection<BackupArchive> archives,
    BackupProviderCatalog providers,
    BackupArchiveAccess access,
    Pole.Features.GitOps.Contracts.IGitOpsMutations git,
    TimeProvider clock
)
{
    public async Task<BackupDestinationDto?> DestinationAsync(
        string tenant,
        string id,
        CancellationToken ct)
    {
        var row = await destinations.Find(BackupsFilters<BackupDestination>.ById(tenant, id)).SingleOrDefaultAsync(ct);
        if (row is null)
            return null;
        return BackupDestinationDto.From(row) with
        {
            GitOps = await git.StatusAsync(new(tenant, BackupDeclarationKinds.Destination, id), ct)
        };
    }

    public async Task<BackupPolicyDto?> PolicyAsync(
        string tenant,
        string id,
        CancellationToken ct)
    {
        var row = await policies.Find(BackupsFilters<BackupPolicy>.ById(tenant, id)).SingleOrDefaultAsync(ct);
        if (row is null)
            return null;
        return BackupViews.From(row, row.ReportSchedule(clock.GetUtcNow())) with
        {
            GitOps = await git.StatusAsync(new(tenant, BackupDeclarationKinds.Policy, id), ct)
        };
    }

    public async Task<BackupOperationDto?> OperationAsync(
        string tenant,
        string id,
        CancellationToken ct)
    {
        var row = await operations.Find(BackupsFilters<BackupOperation>.ById(tenant, id)).SingleOrDefaultAsync(ct);
        if (row is null)
            return null;
        return BackupViews.From(row);
    }

    public async Task<BackupDestinationDto[]> DestinationsAsync(string tenant, CancellationToken ct)
    {
        var rows = await destinations.Find(BackupsFilters<BackupDestination>.Tenant(tenant))
            .SortBy(d => d.Name)
            .ToListAsync(ct);
        var ownership = await git.StatusesAsync(
            new(tenant, BackupDeclarationKinds.Destination, rows.Select(d => d.Id).ToArray()),
            ct);
        return rows.Select(d => BackupDestinationDto.From(d) with { GitOps = ownership.GetValueOrDefault(d.Id) })
            .ToArray();
    }

    public async Task<BackupPolicyDto[]> PoliciesAsync(string tenant, CancellationToken ct)
    {
        var rows = await policies.Find(BackupsFilters<BackupPolicy>.Tenant(tenant)).SortBy(p => p.Name).ToListAsync(ct);
        var ownership = await git.StatusesAsync(
            new(tenant, BackupDeclarationKinds.Policy, rows.Select(p => p.Id).ToArray()),
            ct);
        var now = clock.GetUtcNow();
        return rows.Select(p =>
                BackupViews.From(p, p.ReportSchedule(now)) with { GitOps = ownership.GetValueOrDefault(p.Id) })
            .ToArray();
    }

    public async Task<BackupArchiveDto[]> ArchivesAsync(string tenant, CancellationToken ct) =>
        (await archives.Find(BackupsFilters<BackupArchive>.Tenant(tenant))
            .SortByDescending(a => a.CreatedAt)
            .ToListAsync(ct)).Select(a => BackupViews.From(
            a,
            providers.Find(a.ResourceType)?.SupportsPointInTimeRecovery == true))
        .ToArray();

    public async Task<BackupWriteResult<BackupCatalogEntry[]>> CatalogAsync(
        string tenant,
        string id,
        CancellationToken ct)
    {
        var archive = await archives.Find(BackupsFilters<BackupArchive>.ById(tenant, id)).SingleOrDefaultAsync(ct);
        if (archive is null)
            return new BackupWriteResult<BackupCatalogEntry[]>.Rejected("not-found");
        var provider = providers.Find(archive.ResourceType);
        if (provider is null)
            return new BackupWriteResult<BackupCatalogEntry[]>.Rejected("unsupported-provider");
        var result = await access.ListAsync(archive, provider, ct);
        return result is BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>.Success success
            ? new BackupWriteResult<BackupCatalogEntry[]>.Saved(
                success.Value.OrderByDescending(x => x.CompletedAt).ToArray())
            : new BackupWriteResult<BackupCatalogEntry[]>.Rejected(
                ((BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>.Failure)result).Code);
    }
}
