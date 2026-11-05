using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Application.Declarations;
using Pole.Features.Functions.Application;
using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Runtime.Provisioning;

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
