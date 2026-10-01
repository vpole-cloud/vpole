using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Endpoints.Connectors;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence.Connectors;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Database;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.PrivateAccess.Connectors;

public sealed class ConnectorEnrollmentTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public void ConnectorStatus_UsesFreshContactAndCredentialState()
    {
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var enrollment = DeploymentTargetConnectorEnrollment.Issue(
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            DeploymentTargetConnectorEnrollmentKind.Initial,
            1,
            new string('A', 64),
            now.AddMinutes(-1),
            TimeSpan.FromMinutes(5));
        var connector = DeploymentTargetConnector.Enroll(
            enrollment,
            new string('B', 64),
            new string('C', 64),
            now.AddDays(1),
            now.AddSeconds(-30));

        Assert.Equal("Unknown", connector.ToDto(now).Health);
        Assert.Null(connector.ToDto(now).LastContactAtUtc);

        var lastContact = now.AddSeconds(-9);
        var observed = connector with
        {
            Observation = new DeploymentTargetConnectorObservation(
                "1.2.3",
                "Degraded",
                ImmutableArray.Create("worker-v1"),
                lastContact)
        };
        var fresh = observed.ToDto(now);
        Assert.Equal("Active", fresh.CredentialStatus);
        Assert.Equal("Degraded", fresh.Health);
        Assert.Equal(lastContact, fresh.LastContactAtUtc);
        Assert.Equal("1.2.3", fresh.Version);
        Assert.Equal(["worker-v1"], fresh.Capabilities);

        var stale = observed.ToDto(now.AddSeconds(1));
        Assert.Equal("Disconnected", stale.Health);
        Assert.Equal(lastContact, stale.LastContactAtUtc);

        var revoked = observed.Revoke(now).ToDto(now);
        Assert.Equal("Revoked", revoked.CredentialStatus);
        Assert.Equal("Disconnected", revoked.Health);
        Assert.Equal(lastContact, revoked.LastContactAtUtc);

        var expired = (observed with { CertificateNotAfterUtc = now }).ToDto(now);
        Assert.Equal("Expired", expired.CredentialStatus);
        Assert.Equal("Disconnected", expired.Health);
        Assert.Equal(lastContact, expired.LastContactAtUtc);
    }

    [Fact]
    public async Task Enrollment_ConsumesTargetBoundTokenExactlyOnce_AndStoresOnlyItsDigest()
    {
        var target = await InsertTargetAsync();
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var issueResponse = await Client.PostAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{target.Id}/connectors/enrollments",
            new IssueDeploymentTargetConnectorEnrollmentBody(target.Revision));
        Assert.Equal(HttpStatusCode.OK, issueResponse.StatusCode);
        Assert.Equal("no-store", issueResponse.Headers.CacheControl?.ToString());
        var issued = Assert.IsType<DeploymentTargetConnectorEnrollmentIssued>(
            await issueResponse.Content.ReadFromJsonAsync<DeploymentTargetConnectorEnrollmentIssued>());
        Client.DefaultRequestHeaders.Remove("X-Test-Platform-Admin");

        var secondTarget = await InsertTargetAsync();
        using var certificate = CreateConnectorCertificate(secondTarget.Id);
        var body = new RedeemDeploymentTargetConnectorEnrollmentBody(
            issued.Token,
            Convert.ToBase64String(certificate.Export(X509ContentType.Cert)));
        var foreign = await Client.PostAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{secondTarget.Id}/connectors/enroll",
            body);
        Assert.Equal(HttpStatusCode.Conflict, foreign.StatusCode);

        var url = $"/api/v1/platform/deployment-targets/{target.Id}/connectors/enroll";
        var attempts = await Task.WhenAll(
            Client.PostAsJsonAsync(url, body),
            Client.PostAsJsonAsync(url, body));
        Assert.Single(attempts, attempt => attempt.StatusCode == HttpStatusCode.OK);
        Assert.Single(attempts, attempt => attempt.StatusCode == HttpStatusCode.Conflict);

        var connector = await Fixture.Database
            .GetCollection<DeploymentTargetConnector>("DeploymentTargetConnectors")
            .Find(Builders<DeploymentTargetConnector>.Filter.Empty)
            .SingleAsync();
        Assert.Equal(target.Id, connector.TargetId);
        Assert.IsType<ActiveDeploymentTargetConnectorState>(connector.State);
        Assert.Equal(issued.ConnectorId, connector.Id);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(certificate.RawData)),
            connector.CertificateFingerprintSha256);
        var rawEnrollment = await Fixture.Database
            .GetCollection<BsonDocument>("DeploymentTargetConnectorEnrollments")
            .Find(Builders<BsonDocument>.Filter.Empty)
            .SingleAsync();
        Assert.DoesNotContain(issued.Token, rawEnrollment.ToJson(), StringComparison.Ordinal);
        Assert.NotEqual(BsonNull.Value, rawEnrollment["ConsumedAtUtc"]);
        Assert.DoesNotContain(issued.Token, issued.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(issued.Token, body.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnrollmentIssue_RequiresPlatformAdmin()
    {
        var target = await InsertTargetAsync();
        var response = await Client.PostAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{target.Id}/connectors/enrollments",
            new IssueDeploymentTargetConnectorEnrollmentBody(target.Revision));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UppercaseObjectIdRoutes_ReturnCanonicalIdentityAcrossEnrollmentAndLifecycle()
    {
        var target = await InsertTargetAsync();
        var uppercaseTarget = target.Id.ToUpperInvariant();
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var issued = await IssueAsync(uppercaseTarget, target.Revision);
        Assert.Equal(target.Id, issued.TargetId);
        using var certificate = CreateConnectorCertificate("unrelated-subject");
        var enrolled = await RedeemAsync(uppercaseTarget, issued.Token, certificate);
        Assert.Equal(target.Id, enrolled.TargetId);
        var uppercaseConnector = enrolled.ConnectorId.ToUpperInvariant();

        var broker = new MongoConnectorBrokerCapabilities(
            Fixture.Database.GetCollection<DeploymentTargetConnector>("DeploymentTargetConnectors"),
            Fixture.DbContext.DeploymentTargets,
            TimeProvider.System);
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var identity = Assert.IsType<DeploymentTargetConnectorIdentity>(
            (await broker.ResolveAsync(fingerprint)).Value);
        Assert.Equal(target.Id, identity.TargetId);

        var list = await Client.GetAsync(
            $"/api/v1/platform/deployment-targets/{uppercaseTarget}/connectors");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var items = Assert.IsType<DeploymentTargetConnectorList>(
            await list.Content.ReadFromJsonAsync<DeploymentTargetConnectorList>());
        Assert.Equal(enrolled.ConnectorId, Assert.Single(items.Items).Id);

        var status = await Client.GetAsync(
            $"/api/v1/platform/deployment-targets/{uppercaseTarget}/connectors/{uppercaseConnector}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var rotation = await Client.PostAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{uppercaseTarget}/connectors/{uppercaseConnector}/enrollments",
            new IssueDeploymentTargetConnectorRotationBody(enrolled.Revision));
        Assert.Equal(HttpStatusCode.OK, rotation.StatusCode);
        var rotationIssued = Assert.IsType<DeploymentTargetConnectorEnrollmentIssued>(
            await rotation.Content.ReadFromJsonAsync<DeploymentTargetConnectorEnrollmentIssued>());
        Assert.Equal(target.Id, rotationIssued.TargetId);
        Assert.Equal(enrolled.ConnectorId, rotationIssued.ConnectorId);

        using var replacement = CreateConnectorCertificate("still-unrelated");
        var rotated = await RedeemAsync(uppercaseTarget, rotationIssued.Token, replacement);
        Assert.Equal(target.Id, rotated.TargetId);
        var revoke = await Client.PostAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{uppercaseTarget}/connectors/{uppercaseConnector}/revoke",
            new RevokeDeploymentTargetConnectorBody(rotated.Revision));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var revoked = Assert.IsType<DeploymentTargetConnectorRevoked>(
            await revoke.Content.ReadFromJsonAsync<DeploymentTargetConnectorRevoked>());
        Assert.Equal(target.Id, revoked.TargetId);
        Assert.Equal(enrolled.ConnectorId, revoked.ConnectorId);
    }

    [Fact]
    public async Task Redemption_RechecksTokenExpiryInsideTransactionCallback()
    {
        var target = await InsertTargetAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var tokenDigest = new string('A', 64);
        var enrollment = DeploymentTargetConnectorEnrollment.Issue(
            target.Id,
            ObjectId.GenerateNewId().ToString(),
            DeploymentTargetConnectorEnrollmentKind.Initial,
            1,
            tokenDigest,
            clock.GetUtcNow().UtcDateTime,
            TimeSpan.FromMinutes(1));
        await Fixture.Database
            .GetCollection<DeploymentTargetConnectorEnrollment>("DeploymentTargetConnectorEnrollments")
            .InsertOneAsync(enrollment);

        var transactionRunner = new MongoRunTransaction(Fixture.Client);
        RunMongoOutcomeTransaction<RedeemDeploymentTargetConnectorEnrollmentOutcome> delayedRun =
            (operation, cancellationToken) =>
            {
                clock.Advance(TimeSpan.FromMinutes(2));
                return transactionRunner.InvokeOutcomeAsync(operation, cancellationToken);
            };
        var redeem = new MongoRedeemDeploymentTargetConnectorEnrollment(
            Fixture.DbContext.DeploymentTargets,
            Fixture.Database.GetCollection<DeploymentTargetConnectorEnrollment>("DeploymentTargetConnectorEnrollments"),
            Fixture.Database.GetCollection<DeploymentTargetConnector>("DeploymentTargetConnectors"),
            delayedRun,
            clock);

        var result = await redeem.InvokeAsync(
            target.Id,
            tokenDigest,
            new string('B', 64),
            new string('C', 64),
            clock.GetUtcNow().UtcDateTime.AddDays(1));
        Assert.True(result is DeploymentTargetConnectorEnrollmentRejected);
    }

    [Fact]
    public async Task Redemption_RechecksCertificateValidityInsideTransactionCallback()
    {
        var target = await InsertTargetAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var tokenDigest = new string('D', 64);
        var enrollment = DeploymentTargetConnectorEnrollment.Issue(
            target.Id,
            ObjectId.GenerateNewId().ToString(),
            DeploymentTargetConnectorEnrollmentKind.Initial,
            1,
            tokenDigest,
            clock.GetUtcNow().UtcDateTime,
            TimeSpan.FromMinutes(15));
        await Fixture.Database
            .GetCollection<DeploymentTargetConnectorEnrollment>("DeploymentTargetConnectorEnrollments")
            .InsertOneAsync(enrollment);

        var certificateNotAfterUtc = clock.GetUtcNow().UtcDateTime.AddHours(1).AddMinutes(1);
        var transactionRunner = new MongoRunTransaction(Fixture.Client);
        RunMongoOutcomeTransaction<RedeemDeploymentTargetConnectorEnrollmentOutcome> delayedRun =
            (operation, cancellationToken) =>
            {
                clock.Advance(TimeSpan.FromMinutes(2));
                return transactionRunner.InvokeOutcomeAsync(operation, cancellationToken);
            };
        var redeem = new MongoRedeemDeploymentTargetConnectorEnrollment(
            Fixture.DbContext.DeploymentTargets,
            Fixture.Database.GetCollection<DeploymentTargetConnectorEnrollment>("DeploymentTargetConnectorEnrollments"),
            Fixture.Database.GetCollection<DeploymentTargetConnector>("DeploymentTargetConnectors"),
            delayedRun,
            clock);

        var result = await redeem.InvokeAsync(
            target.Id,
            tokenDigest,
            new string('E', 64),
            new string('F', 64),
            certificateNotAfterUtc);
        Assert.True(result is DeploymentTargetConnectorCertificateRejected);
    }

    [Fact]
    public async Task RotationRevocationAndContact_EnforceExactPinAndGeneration()
    {
        var target = await InsertTargetAsync();
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var issued = await IssueAsync(target.Id, target.Revision);
        using var firstCertificate = CreateConnectorCertificate(ObjectId.GenerateNewId().ToString());
        var first = await RedeemAsync(target.Id, issued.Token, firstCertificate);
        Assert.Equal(1, first.Generation);

        var broker = new MongoConnectorBrokerCapabilities(
            Fixture.Database.GetCollection<DeploymentTargetConnector>("DeploymentTargetConnectors"),
            Fixture.DbContext.DeploymentTargets,
            TimeProvider.System);
        var firstFingerprint = Convert.ToHexString(SHA256.HashData(firstCertificate.RawData));
        var resolved = await broker.ResolveAsync(firstFingerprint);
        var identity = Assert.IsType<DeploymentTargetConnectorIdentity>(resolved.Value);
        Assert.Equal(target.Id, identity.TargetId);
        Assert.Equal(first.ConnectorId, identity.ConnectorId);
        Assert.IsType<DeploymentTargetConnectorIdentity>((await broker.ResolveAsync(firstFingerprint)).Value);

        var contactTime = DateTime.UtcNow;
        var contact = new DeploymentTargetConnectorContact(
            target.Id,
            first.ConnectorId,
            first.Generation,
            "1.2.3",
            "Healthy",
            ["worker-v1", "worker-v1"],
            contactTime);
        Assert.True((await broker.RecordContactAsync(contact)) is DeploymentTargetConnectorContactRecorded);
        Assert.True((await broker.RecordContactAsync(contact with { Version = "stale" }))
            is DeploymentTargetConnectorRejected);
        var statusResponse = await Client.GetAsync(
            $"/api/v1/platform/deployment-targets/{target.Id}/connectors/{first.ConnectorId}");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var status = Assert.IsType<DeploymentTargetConnectorDto>(
            await statusResponse.Content.ReadFromJsonAsync<DeploymentTargetConnectorDto>());
        Assert.Equal("1.2.3", status.Version);
        Assert.Equal(["worker-v1"], status.Capabilities);
        Assert.NotNull(status.LastContactAtUtc);
        Assert.InRange(
            Math.Abs((status.LastContactAtUtc.Value - contactTime).TotalMilliseconds),
            0,
            1);
        var statusJson = await statusResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(firstFingerprint, statusJson, StringComparison.Ordinal);
        Assert.DoesNotContain(issued.Token, statusJson, StringComparison.Ordinal);

        var rotationResponse = await Client.PostAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{target.Id}/connectors/{first.ConnectorId}/enrollments",
            new IssueDeploymentTargetConnectorRotationBody(first.Revision));
        Assert.Equal(HttpStatusCode.OK, rotationResponse.StatusCode);
        Assert.Equal("no-store", rotationResponse.Headers.CacheControl?.ToString());
        var rotation = Assert.IsType<DeploymentTargetConnectorEnrollmentIssued>(
            await rotationResponse.Content.ReadFromJsonAsync<DeploymentTargetConnectorEnrollmentIssued>());
        using var secondCertificate = CreateConnectorCertificate(target.Id);
        var second = await RedeemAsync(target.Id, rotation.Token, secondCertificate);
        Assert.Equal(2, second.Generation);
        Assert.True((await broker.ResolveAsync(firstFingerprint)) is DeploymentTargetConnectorRejected);
        var secondFingerprint = Convert.ToHexString(SHA256.HashData(secondCertificate.RawData));
        var secondIdentity = Assert.IsType<DeploymentTargetConnectorIdentity>(
            (await broker.ResolveAsync(secondFingerprint)).Value);
        Assert.Equal(2, secondIdentity.Generation);
        Assert.True((await broker.RecordContactAsync(contact with
        {
            Generation = 1,
            ObservedAtUtc = DateTime.UtcNow
        })) is DeploymentTargetConnectorRejected);

        var revokeResponse = await Client.PostAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{target.Id}/connectors/{first.ConnectorId}/revoke",
            new RevokeDeploymentTargetConnectorBody(second.Revision));
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);
        Assert.True((await broker.ResolveAsync(secondFingerprint)) is DeploymentTargetConnectorRejected);
        var persisted = await Fixture.Database
            .GetCollection<DeploymentTargetConnector>("DeploymentTargetConnectors")
            .Find(item => item.Id == first.ConnectorId)
            .SingleAsync();
        Assert.IsType<RevokedDeploymentTargetConnectorState>(persisted.State);
        var listResponse = await Client.GetAsync(
            $"/api/v1/platform/deployment-targets/{target.Id}/connectors");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = Assert.IsType<DeploymentTargetConnectorList>(
            await listResponse.Content.ReadFromJsonAsync<DeploymentTargetConnectorList>());
        Assert.Equal("Revoked", Assert.Single(list.Items).CredentialStatus);
    }

    [Fact]
    public async Task TargetDeletion_DeniesActiveConnectorUntilRevoked()
    {
        var target = await InsertTargetAsync();
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var issued = await IssueAsync(target.Id, target.Revision);
        using var certificate = CreateConnectorCertificate(target.Id);
        var connector = await RedeemAsync(target.Id, issued.Token, certificate);

        var denied = await Client.DeleteAsync($"/api/v1/platform/deployment-targets/{target.Id}");
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var revoke = await Client.PostAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{target.Id}/connectors/{connector.ConnectorId}/revoke",
            new RevokeDeploymentTargetConnectorBody(connector.Revision));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var deleted = await Client.DeleteAsync($"/api/v1/platform/deployment-targets/{target.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task DisabledOrDeletingTarget_DeniesConnectorIdentityAndEnrollment()
    {
        var target = await InsertTargetAsync();
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var issued = await IssueAsync(target.Id, target.Revision);
        using var certificate = CreateConnectorCertificate(target.Id);
        var connector = await RedeemAsync(target.Id, issued.Token, certificate);
        var broker = new MongoConnectorBrokerCapabilities(
            Fixture.Database.GetCollection<DeploymentTargetConnector>("DeploymentTargetConnectors"),
            Fixture.DbContext.DeploymentTargets,
            TimeProvider.System);
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));

        await Fixture.DbContext.DeploymentTargets.UpdateOneAsync(
            Builders<DeploymentTarget>.Filter.Eq(item => item.Id, target.Id),
            Builders<DeploymentTarget>.Update.Set(item => item.Enabled, false));
        Assert.True((await broker.ResolveAsync(fingerprint)) is DeploymentTargetConnectorRejected);
        Assert.True((await broker.RecordContactAsync(new DeploymentTargetConnectorContact(
            target.Id,
            connector.ConnectorId,
            connector.Generation,
            "1.0.0",
            "Healthy",
            ["worker-v1"],
            DateTime.UtcNow))) is DeploymentTargetConnectorRejected);

        await Fixture.DbContext.DeploymentTargets.UpdateOneAsync(
            Builders<DeploymentTarget>.Filter.Eq(item => item.Id, target.Id),
            Builders<DeploymentTarget>.Update
                .Set(item => item.Enabled, true)
                .Set(item => item.DeletionRequestedAtUtc, DateTime.UtcNow));
        Assert.True((await broker.ResolveAsync(fingerprint)) is DeploymentTargetConnectorRejected);
        var rejectedIssue = await Client.PostAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{target.Id}/connectors/enrollments",
            new IssueDeploymentTargetConnectorEnrollmentBody(target.Revision));
        Assert.Equal(HttpStatusCode.Conflict, rejectedIssue.StatusCode);
    }

    private async Task<DeploymentTargetConnectorEnrollmentIssued> IssueAsync(
        string targetId,
        long targetRevision)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{targetId}/connectors/enrollments",
            new IssueDeploymentTargetConnectorEnrollmentBody(targetRevision));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<DeploymentTargetConnectorEnrollmentIssued>(
            await response.Content.ReadFromJsonAsync<DeploymentTargetConnectorEnrollmentIssued>());
    }

    private async Task<DeploymentTargetConnectorEnrolled> RedeemAsync(
        string targetId,
        string token,
        X509Certificate2 certificate)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{targetId}/connectors/enroll",
            new RedeemDeploymentTargetConnectorEnrollmentBody(
                token,
                Convert.ToBase64String(certificate.Export(X509ContentType.Cert))));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<DeploymentTargetConnectorEnrolled>(
            await response.Content.ReadFromJsonAsync<DeploymentTargetConnectorEnrolled>());
    }

    private async Task<DeploymentTarget> InsertTargetAsync()
    {
        var now = DateTime.UtcNow;
        var target = DeploymentTargetFactory.Create(
            "Connector test target",
            ObjectId.GenerateNewId().ToString(),
            DeploymentTargetConnectionFactory.CreateInCluster(1),
            DeploymentTargetHealth.Unknown(1),
            DeploymentTargetCapabilities.Unknown(1),
            DeploymentTargetWorkloadConfiguration.Create(null, DeploymentTargetNetworkSources.None),
            enabled: true,
            acceptsWorkloads: true,
            new DeploymentTargetFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        return target;
    }

    private static X509Certificate2 CreateConnectorCertificate(string claimedTargetId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            $"CN={claimedTargetId}",
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") },
            true));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
