using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Runtime.Provisioning;

public sealed record FunctionRevisionProvisioningSubject(
    FunctionApp App,
    FunctionRevision Revision);

public delegate Task<FunctionRevisionProvisioningSubject?> LoadFunctionRevisionProvisioningSubject(
    string tenantId,
    string functionAppId,
    string revisionId,
    CancellationToken cancellationToken = default);

public sealed class MongoLoadFunctionRevisionProvisioningSubject(
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionRevision> functionRevisions)
{
    public async Task<FunctionRevisionProvisioningSubject?> InvokeAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken = default)
    {
        var app = await functionApps.Find(
                FunctionRevisionProvisioningSubjectFilters.AppByOwner(tenantId, functionAppId))
            .FirstOrDefaultAsync(cancellationToken);
        if (app is null)
            return null;

        var revision = await functionRevisions.Find(
                FunctionRevisionProvisioningSubjectFilters.RevisionByOwner(
                    tenantId,
                    functionAppId,
                    revisionId))
            .FirstOrDefaultAsync(cancellationToken);
        return revision is null ? null : new FunctionRevisionProvisioningSubject(app, revision);
    }
}

internal static class FunctionRevisionProvisioningSubjectFilters
{
    public static FilterDefinition<FunctionApp> AppByOwner(string tenantId, string functionAppId) =>
        Builders<FunctionApp>.Filter.And(
            Builders<FunctionApp>.Filter.Eq(app => app.TenantId, tenantId),
            Builders<FunctionApp>.Filter.Eq(app => app.Id, functionAppId));

    public static FilterDefinition<FunctionRevision> RevisionByOwner(
        string tenantId,
        string functionAppId,
        string revisionId) =>
        Builders<FunctionRevision>.Filter.And(
            Builders<FunctionRevision>.Filter.Eq(revision => revision.TenantId, tenantId),
            Builders<FunctionRevision>.Filter.Eq(revision => revision.FunctionAppId, functionAppId),
            Builders<FunctionRevision>.Filter.Eq(revision => revision.Id, revisionId));
}
