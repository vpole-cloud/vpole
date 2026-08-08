using MongoDB.Driver;
using Pole.Features.WebServices.Domain;

namespace Pole.Tests.Integration.Infrastructure;

internal static class WebServiceTestPersistence
{
    public static IMongoCollection<WebService> WebServices(
        this IMongoDatabase database) =>
        database.GetCollection<WebService>("WebServices");

    public static IMongoCollection<WebServiceRelease> WebServiceReleases(
        this IMongoDatabase database) =>
        database.GetCollection<WebServiceRelease>("WebServiceReleases");

    public static IMongoCollection<WebServiceDeploymentToken> WebServiceDeploymentTokens(
        this IMongoDatabase database) =>
        database.GetCollection<WebServiceDeploymentToken>("WebServiceDeploymentTokens");
}
