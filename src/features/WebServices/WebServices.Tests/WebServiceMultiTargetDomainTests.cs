using System.Collections.Immutable;
using MongoDB.Bson;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Domain;
using Xunit;

namespace Pole.Features.WebServices.Tests;

public sealed class WebServiceMultiTargetDomainTests
{
    [Fact]
    public void DesiredState_NoOpKeepsRevision_AndPlacementOnlyAdvanceIncrementsItOnce()
    {
        var service = CreateService();

        var unchanged = service.UpdateDesiredState(service.Spec, ["production"]);
        var placementChanged = unchanged.AdvancePlacementDesiredState();

        Assert.Same(service, unchanged);
        Assert.Equal(service.ActiveRevision + 1, placementChanged.ActiveRevision);
        Assert.Equal(service.Revision + 1, placementChanged.Revision);
    }

    [Fact]
    public void ReleaseCompletion_AggregatesPartialFailure_AndIgnoresDuplicateOrStaleCompletion()
    {
        var revision = 4L;
        var first = Entry("507f1f77bcf86cd799439101", "507f1f77bcf86cd799439201", revision, 6);
        var second = Entry("507f1f77bcf86cd799439102", "507f1f77bcf86cd799439202", revision, 3);
        var release = WebServiceReleaseFactory.CreatePending(
            ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(), revision,
            WebServiceReleaseAction.Update, null, null, CreateService().Spec, [first, second],
            null, null, new WebServiceReleaseFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(), DateTime.UtcNow),
            CreateService().Exposure);

        var succeeded = WebServiceReleaseFactory.ApplyCompletion(
            release, first.ResourceDeploymentId, revision, 6, WebServiceReleaseDeploymentOperation.Apply,
            succeeded: true, retryable: false, failureCode: null, failureMessage: null, completedAtUtc: DateTime.UtcNow);
        var completed = WebServiceReleaseFactory.ApplyCompletion(
            succeeded, second.ResourceDeploymentId, revision, 3, WebServiceReleaseDeploymentOperation.Apply,
            succeeded: false, retryable: false, failureCode: "ApplyFailed", failureMessage: "cluster unavailable", completedAtUtc: DateTime.UtcNow);
        var duplicate = WebServiceReleaseFactory.ApplyCompletion(
            completed, second.ResourceDeploymentId, revision, 3, WebServiceReleaseDeploymentOperation.Apply,
            succeeded: false, retryable: false, failureCode: "ApplyFailed", failureMessage: "cluster unavailable", completedAtUtc: DateTime.UtcNow);
        var stale = WebServiceReleaseFactory.ApplyCompletion(
            completed, second.ResourceDeploymentId, revision - 1, 3, WebServiceReleaseDeploymentOperation.Apply,
            succeeded: true, retryable: false, failureCode: null, failureMessage: null, completedAtUtc: DateTime.UtcNow);

        Assert.Equal(WebServiceReleaseOutcome.PartiallyFailed, completed.Outcome);
        Assert.Equal("cluster unavailable", completed.FailureMessage);
        Assert.Equal(completed, duplicate);
        Assert.Equal(completed, stale);
    }

    [Fact]
    public void ReleaseCompletion_PreservesDeterministicFailureMessage_WhenSuccessCompletesLast()
    {
        var revision = 4L;
        var failedEntry = Entry("507f1f77bcf86cd799439101", "507f1f77bcf86cd799439201", revision, 6);
        var succeededEntry = Entry("507f1f77bcf86cd799439102", "507f1f77bcf86cd799439202", revision, 3);
        var release = WebServiceReleaseFactory.CreatePending(
            ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(), revision,
            WebServiceReleaseAction.Update, null, null, CreateService().Spec, [failedEntry, succeededEntry],
            null, null, new WebServiceReleaseFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(), DateTime.UtcNow),
            CreateService().Exposure);

        var partiallyCompleted = WebServiceReleaseFactory.ApplyCompletion(
            release, failedEntry.ResourceDeploymentId, revision, 6,
            WebServiceReleaseDeploymentOperation.Apply,
            succeeded: false, retryable: false, failureCode: "ApplyFailed",
            failureMessage: "cluster unavailable", completedAtUtc: DateTime.UtcNow);
        var completed = WebServiceReleaseFactory.ApplyCompletion(
            partiallyCompleted, succeededEntry.ResourceDeploymentId, revision, 3,
            WebServiceReleaseDeploymentOperation.Apply,
            succeeded: true, retryable: false, failureCode: null,
            failureMessage: null, completedAtUtc: DateTime.UtcNow);

        Assert.Equal(WebServiceReleaseOutcome.PartiallyFailed, completed.Outcome);
        Assert.Equal("cluster unavailable", completed.FailureMessage);
    }

    private static WebServiceReleaseDeployment Entry(string deploymentId, string targetId, long revision, long generation) =>
        new(deploymentId, targetId, revision, generation, WebServiceReleaseDeploymentOperation.Apply,
            WebServiceReleaseDeploymentOutcome.Pending, null, null, null);

    private static WebService CreateService()
    {
        var probe = new ProbeConfig("/health", "8080");
        return WebServiceFactory.Create(
            "api", ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            new WebServiceSpec(
                new ImageInfo("registry.example.test", "team/api", "latest",
                    SecretName: null),
                new WebServiceConfig()
                {
                    EnvironmentVariables = ImmutableDictionary<string, string>.Empty,
                    PortMappings = ImmutableList<PortMapping>.Empty,
                    ConnectionEnvironmentReferences = ImmutableDictionary<string, ConnectionEnvironmentReference>.Empty,
                    SecretEnvironmentReferences = ImmutableDictionary<string, SecretEnvironmentReference>.Empty
                },
                null, new ProbeSpec(probe, probe, probe)),
            WebServiceExposure.Create(WebServiceExposureMode.Public),
            WebServiceFactory.DefaultOptions with
            {
                Id = ObjectId.GenerateNewId().ToString(),
                Tags = ImmutableHashSet.Create("production")
            });
    }
}
