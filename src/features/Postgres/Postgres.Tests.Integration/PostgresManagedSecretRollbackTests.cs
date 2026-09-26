using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Quotas.Domain;
using Pole.Features.Quotas.Primitives;
using Pole.Features.Postgres.Tests.Integration.Infrastructure;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.ResultType;
using Xunit;

namespace Pole.Features.Postgres.Tests.Integration;

public sealed class PostgresManagedSecretRollbackTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        var providerRegistration = services.Last(descriptor => descriptor.ServiceType == typeof(CreateBoundManagedSecret));
        services.AddScoped<CreateBoundManagedSecret>(provider =>
        {
            var create = (CreateBoundManagedSecret)providerRegistration.ImplementationFactory!(provider);
            return async (prepared, group, scope, deployment, session, ct) =>
            {
                var written = await create(prepared, group, scope, deployment, session, ct);
                Assert.IsType<ManagedSecretMutationSucceeded>(written.Value);
                Assert.Equal(1, await Fixture.Database.GetCollection<Secret>("Secrets")
                    .CountDocumentsAsync(session, secret => secret.Id == prepared.SecretId, cancellationToken: ct));
                return new ManagedSecretChanged();
            };
        });
    }

    [Fact]
    public async Task FailureAfterManagedBindingWriteRollsBackOwnerPlacementSecretVersionsAndOutbox()
    {
        var group = await DbContext.InsertResourceGroupAsync(PostgresTestData.SeedResourceGroup());
        var outboxBefore = await DbContext.OutboxMessages.CountDocumentsAsync(_ => true);
        var result = await Mediator.Send(PostgresTestData.CreatePostgresClusterRequest(group.Id));
        Assert.IsType<CreatePostgresClusterOutcome.CreatePostgresClusterRejected>(result.AssertSuccess());
        Assert.Equal(0, await DbContext.Postgres.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await DbContext.ResourceDeployments.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await DbContext.ResourceRegistrations.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await Fixture.Database.GetCollection<Secret>("Secrets").CountDocumentsAsync(_ => true));
        Assert.Equal(0, await Fixture.Database.GetCollection<SecretVersion>("SecretVersions").CountDocumentsAsync(_ => true));
        Assert.Equal(outboxBefore, await DbContext.OutboxMessages.CountDocumentsAsync(_ => true));
        var usage = await Fixture.Database.GetCollection<TenantQuotaUsage>("TenantQuotaUsages")
            .Find(item => item.TenantId == PostgresTestData.TenantId).FirstOrDefaultAsync();
        Assert.Equal(0, usage is null ? 0 : QuotaValues.GetValue(usage.Values, PostgresClusterCountQuota.QuotaKey));
        Assert.Equal(0, await Fixture.Database.GetCollection<BsonDocument>("PostgresCredentials").CountDocumentsAsync(new BsonDocument()));
    }
}
