using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Domain;
using Pole.Features.WebServices.Application.Consumers;
using Pole.Features.WebServices.Application.Deployments;
using Pole.Features.WebServices.Domain;
using Pole.Testing;
using Xunit;

namespace Pole.Features.WebServices.Tests.Integration;

[CollectionDefinition(nameof(WebServicesMongoCollection))]
public sealed class WebServicesMongoCollection : ICollectionFixture<WebServicesMongoFixture>;

public sealed class WebServicesMongoFixture : MongoReplicaSetFixture
{
    internal IMongoCollection<WebService> WebServices =>
        Database.GetCollection<WebService>("WebServices");

    internal IMongoCollection<WebServiceRelease> Releases =>
        Database.GetCollection<WebServiceRelease>("WebServiceReleases");

    internal IMongoCollection<OutboxMessage> OutboxMessages =>
        Database.GetCollection<OutboxMessage>("OutboxMessages");

    internal WebServiceDeploymentConsumer CreateDeploymentConsumer()
    {
        var complete = new MongoCompleteWebServiceDeployment(
            Releases,
            WebServices,
            new MongoRunTransaction(Client).InvokeOutcomeAsync<CompleteWebServiceDeploymentOutcome>,
            StoreOutboxWrites,
            loadHostnameBindingsForDeployment: (_, _, _) =>
                throw new InvalidOperationException("Unexpected hostname lookup."),
            deleteHostnameBindingsForDeployment: (_, _, _) =>
                throw new InvalidOperationException("Unexpected hostname cleanup."),
            getExactResourceDeploymentFinalizationReadiness: (_, _, _) =>
                throw new InvalidOperationException("Unexpected deletion readiness check."),
            purgeExactDeletedResourceDeployment: (_, _, _) =>
                throw new InvalidOperationException("Unexpected deployment purge."));
        return new WebServiceDeploymentConsumer(complete.InvokeAsync, TimeProvider.System);
    }
}
