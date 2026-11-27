// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.ContainerRegistries.Application.Credentials;
using Pole.Features.ContainerRegistries.Application.Endpoints;
using Pole.Features.ContainerRegistries.Application.Projects;
using Pole.Features.ContainerRegistries.Application.Provisioning;
using Pole.Features.ContainerRegistries.Application.Security;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.Tests.Integration.Http;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ContainerRegistries;

// Rewrap and cleanup reconciliation scan all rows; wrapping key rings belong to one test host.
[Collection(IntegrationTestCollection.Global2)]
public sealed class ContainerRegistryProvisioningTests(
    MongoReplicaSetFixture fixture
) : IsolatedApiIntegrationTestBase(fixture)
{
    private const string AttemptIdOne = "11111111111111111111111111111111";
    private const string AttemptIdTwo = "22222222222222222222222222222222";

    [Fact]
    public async Task Provisioning_PersistsOnlyProtectedCredentialsAndIsIdempotent()
    {
        var registryClient = new FakeContainerRegistryClient();
        using var encryption = TestEnvelopeEncryptionScope.Create();
        var protector = new EnvelopeProtectRegistryCredentialSecret(encryption.Service);
        var service = CreateProvisioningService(registryClient, protector.InvokeAsync);

        var first = await service(Data.TenantId, Data.UserId);
        var second = await service(Data.TenantId, Data.UserId);

        var project = Assert.IsType<RegistryProject>(first.Value);
        var sameProject = Assert.IsType<RegistryProject>(second.Value);
        Assert.Equal(project.Id, sameProject.Id);
        Assert.Equal(1, registryClient.ProvisionCalls);
        var ready = Assert.IsType<ReadyRegistryProject>(project.State);

        var credentials = await Fixture.DbContext
            .RegistryCredentials
            .Find(item => item.RegistryProjectId == project.Id)
            .ToListAsync();
        Assert.Equal(2, credentials.Count);
        Assert.All(
            credentials,
            credential =>
            {
                Assert.Equal(
                    LocalKeyFileKeyEncryptionProvider.StableProviderId,
                    credential.EncryptedSecret.KeyProviderId);
                Assert.Equal("integration", credential.EncryptedSecret.KeyEncryptionKeyId);
                Assert.Equal(1, credential.EncryptedSecret.KeyEncryptionKeyVersion);
                Assert.Equal(EnvelopeEncryptionService.CurrentFormatVersion, credential.EncryptedSecret.FormatVersion);
            });

        var rawCredentials = await Fixture.Database
            .GetCollection<BsonDocument>("RegistryCredentials")
            .Find(Builders<BsonDocument>.Filter.Eq("RegistryProjectId", project.Id))
            .ToListAsync();
        Assert.Equal(2, rawCredentials.Count);
        Assert.All(rawCredentials, AssertRawCredentialDoesNotContainPlaintext);

        LoadRegistryCredential loadCredential = async (
            tenantId, registryProjectId,
            purpose, cancellationToken) => await Fixture.DbContext
            .RegistryCredentials
            .Find(item => item.TenantId == tenantId && item.RegistryProjectId == registryProjectId &&
                item.Purpose == purpose)
            .FirstOrDefaultAsync(cancellationToken);
        var resolver = new RegistryCredentialResolver(
            loadCredential,
            new EnvelopeUnprotectRegistryCredentialSecret(encryption.Service).InvokeAsync,
            TimeProvider.System);
        var resolved = await resolver.InvokeAsync(Data.TenantId, project.Id, RegistryCredentialPurpose.BuildPushPull);
        var buildCredential = Assert.IsType<ResolvedRegistryCredential>(resolved.Value);
        Assert.Equal(FakeContainerRegistryClient.BuildSecret, buildCredential.Secret);
        Assert.Equal(
            ready.BuildCredentialId,
            credentials.Single(item => item.Purpose == RegistryCredentialPurpose.BuildPushPull).Id);
    }

    [Fact]
    public async Task FailedProvisioning_PersistsNameOnlyCleanupBeforeRetry()
    {
        var registryClient = new FakeContainerRegistryClient
        {
            FailNextProvision = true,
        };
        using var encryption = TestEnvelopeEncryptionScope.Create();
        var protector = new EnvelopeProtectRegistryCredentialSecret(encryption.Service);
        var service = CreateProvisioningService(registryClient, protector.InvokeAsync);

        var failed = await service(Data.TenantId, Data.UserId);

        Assert.IsType<ContainerRegistryProviderError>(failed.Value);
        var storedFailure = await Fixture.DbContext
            .RegistryProjects
            .Find(item => item.TenantId == Data.TenantId)
            .SingleAsync();
        var cleanup = Assert.IsType<CleanupRequiredRegistryProjectProvisioning>(storedFailure.State);
        var expectedNames = RegistryRobotNames.ForAttempt(cleanup.AttemptId);
        Assert.Equal(expectedNames.Build, cleanup.BuildRobotName);
        Assert.Equal(expectedNames.Pull, cleanup.PullRobotName);
        Assert.Null(cleanup.BuildExternalRobotId);
        Assert.Null(cleanup.PullExternalRobotId);
        Assert.True(cleanup.CleanupNotBeforeUtc > cleanup.CleanupRequiredAtUtc);
        Assert.Equal(0, registryClient.RobotCleanupCalls);

        var blockedRetry = await service(Data.TenantId, Data.UserId);

        Assert.IsType<TenantContainerRegistryProvisioningConflict>(blockedRetry.Value);
        Assert.Equal(1, registryClient.ProvisionCalls);

        var cleanupService = CreateCleanupService(
            registryClient,
            new FixedTimeProvider(new DateTimeOffset(cleanup.CleanupNotBeforeUtc.AddSeconds(1), TimeSpan.Zero)));
        await cleanupService.ReconcileAsync();
        Assert.Equal(1, registryClient.RobotCleanupCalls);
        var afterEmptySweep
            = await Fixture.DbContext.RegistryProjects.Find(item => item.Id == storedFailure.Id).SingleAsync();
        var pendingAfterEmptySweep = Assert.IsType<CleanupRequiredRegistryProjectProvisioning>(afterEmptySweep.State);

        registryClient.MatchedByNameCounts.Enqueue(1);
        cleanupService = CreateCleanupService(
            registryClient,
            new FixedTimeProvider(
                new DateTimeOffset(pendingAfterEmptySweep.CleanupNotBeforeUtc.AddSeconds(1), TimeSpan.Zero)));
        await cleanupService.ReconcileAsync();
        Assert.Equal(2, registryClient.RobotCleanupCalls);
        var afterDelayedRobot
            = await Fixture.DbContext.RegistryProjects.Find(item => item.Id == storedFailure.Id).SingleAsync();
        var pendingAfterDelayedRobot
            = Assert.IsType<CleanupRequiredRegistryProjectProvisioning>(afterDelayedRobot.State);

        var finalSweepAt = new[]
            {
                pendingAfterDelayedRobot.CleanupNotBeforeUtc,
                pendingAfterDelayedRobot.CleanupVerificationNotBeforeUtc,
            }.Max()
            .AddSeconds(1);
        cleanupService = CreateCleanupService(
            registryClient,
            new FixedTimeProvider(new DateTimeOffset(finalSweepAt, TimeSpan.Zero)));
        await cleanupService.ReconcileAsync();
        Assert.Equal(3, registryClient.RobotCleanupCalls);
        Assert.Equal([expectedNames.Build, expectedNames.Pull], registryClient.DeletedRobotNames.Take(2));

        var retried = await service(Data.TenantId, Data.UserId);

        var ready = Assert.IsType<RegistryProject>(retried.Value);
        Assert.IsType<ReadyRegistryProject>(ready.State);
        Assert.Equal(2, registryClient.ProvisionCalls);
    }

    [Fact]
    public async Task CredentialProtector_ReturnsFailureForTamperedPayloadAndTenantSubstitution()
    {
        using var encryption = TestEnvelopeEncryptionScope.Create();
        var protector = new EnvelopeProtectRegistryCredentialSecret(encryption.Service);
        var unprotector = new EnvelopeUnprotectRegistryCredentialSecret(encryption.Service);
        var credentialId = ObjectId.GenerateNewId().ToString();
        var protectedResult = await protector.InvokeAsync(Data.TenantId, credentialId, 1, "secret");
        var protectedSecret = Assert.IsType<EncryptedValue>(protectedResult.Value);
        var credential = RegistryCredentialFactory.Create(
            Data.TenantId,
            ObjectId.GenerateNewId().ToString(),
            RegistryCredentialPurpose.BuildPushPull,
            "robot-id",
            "robot-user",
            1,
            protectedSecret,
            null,
            new RegistryCredentialFactory.CreateOptions(credentialId, DateTime.UtcNow));

        var tamperedTag = protectedSecret.AuthenticationTag.ToArray();
        tamperedTag[0] ^= 0x01;
        var tampered = credential with
        {
            EncryptedSecret = protectedSecret with { AuthenticationTag = tamperedTag },
        };

        var tamperedResult = await unprotector.InvokeAsync(tampered);
        var substitutedTenantResult = await unprotector.InvokeAsync(credential with { TenantId = "other-tenant" });

        Assert.IsType<RegistryCredentialUnavailable>(tamperedResult.Value);
        Assert.IsType<RegistryCredentialUnavailable>(substitutedTenantResult.Value);
        Assert.DoesNotContain("secret", credential.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CredentialRewrap_ChangesOnlyWrappedKeyAndIsIdempotent()
    {
        using var encryption = RotationEncryptionScope.Create();
        var credentialId = ObjectId.GenerateNewId().ToString();
        var oldProtector = new EnvelopeProtectRegistryCredentialSecret(encryption.OldService);
        var encryptedResult = await oldProtector.InvokeAsync(Data.TenantId, credentialId, 1, "rotation-secret");
        var oldEnvelope = Assert.IsType<EncryptedValue>(encryptedResult.Value);
        var credential = RegistryCredentialFactory.Create(
            Data.TenantId,
            ObjectId.GenerateNewId().ToString(),
            RegistryCredentialPurpose.BuildPushPull,
            "robot-id",
            "robot-user",
            1,
            oldEnvelope,
            null,
            new RegistryCredentialFactory.CreateOptions(credentialId, DateTime.UtcNow));
        await Fixture.DbContext.RegistryCredentials.InsertOneAsync(credential);

        var service = new RegistryCredentialRewrapService(
            new RegistryCredentialRewrapPersistence(
                Fixture.Database.GetCollection<RegistryCredential>("RegistryCredentials")),
            encryption.RotatingService);
        var wrongTarget = encryption.RotatingService.ActiveKey with { KeyVersion = 99 };
        var rejected = await service.InvokeAsync(wrongTarget);
        Assert.IsType<RegistryCredentialRewrapRejected>(rejected.Value);

        var first = await service.InvokeAsync(encryption.RotatingService.ActiveKey);
        var firstSummary = Assert.IsType<RegistryCredentialRewrapSummary>(first.Value);
        Assert.Equal(1, firstSummary.Examined);
        Assert.Equal(1, firstSummary.Rewrapped);
        Assert.Equal(0, firstSummary.ConcurrentChanges);

        var rewrapped = await Fixture.DbContext.RegistryCredentials.Find(item => item.Id == credentialId).SingleAsync();
        Assert.Equal("new", rewrapped.EncryptedSecret.KeyEncryptionKeyId);
        Assert.Equal(2, rewrapped.EncryptedSecret.KeyEncryptionKeyVersion);
        Assert.True(oldEnvelope.Ciphertext.SequenceEqual(rewrapped.EncryptedSecret.Ciphertext));
        Assert.True(oldEnvelope.Nonce.SequenceEqual(rewrapped.EncryptedSecret.Nonce));
        Assert.True(oldEnvelope.AuthenticationTag.SequenceEqual(rewrapped.EncryptedSecret.AuthenticationTag));

        var newOnlyResult
            = await new EnvelopeUnprotectRegistryCredentialSecret(encryption.NewOnlyService).InvokeAsync(rewrapped);
        var plaintext = Assert.IsType<string>(newOnlyResult.Value);
        Assert.Equal("rotation-secret", plaintext);
        var oldOnlyResult
            = await new EnvelopeUnprotectRegistryCredentialSecret(encryption.OldService).InvokeAsync(rewrapped);
        Assert.IsType<RegistryCredentialUnavailable>(oldOnlyResult.Value);

        var second = await service.InvokeAsync(encryption.RotatingService.ActiveKey);
        var secondSummary = Assert.IsType<RegistryCredentialRewrapSummary>(second.Value);
        Assert.Equal(1, secondSummary.Examined);
        Assert.Equal(0, secondSummary.Rewrapped);
        Assert.Equal(0, secondSummary.ConcurrentChanges);
    }

    [Fact]
    public async Task ProvisioningCancellationAfterRemoteCreationCompensatesAndMarksFailed()
    {
        var registryClient = new FakeContainerRegistryClient();
        var service = CreateProvisioningService(registryClient, CancelProtectAsync);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service(Data.TenantId, Data.UserId));

        Assert.Equal(1, registryClient.RobotCleanupCalls);
        Assert.Equal(["21", "22"], registryClient.DeletedRobotIds);
        Assert.Equal(0, registryClient.DeleteProjectCalls);
        var project = await Fixture.DbContext
            .RegistryProjects
            .Find(item => item.TenantId == Data.TenantId)
            .SingleAsync();
        Assert.IsType<FailedRegistryProjectProvisioning>(project.State);
    }

    [Fact]
    public async Task FailedRobotCleanupIsPersistedAndRetriedWithoutDeletingTheProject()
    {
        var registryClient = new FakeContainerRegistryClient { FailRobotCleanup = true };
        var cleanupService = CreateCleanupService(registryClient);
        var service = CreateProvisioningService(registryClient, CancelProtectAsync);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service(Data.TenantId, Data.UserId));

        var pending = await Fixture.DbContext
            .RegistryProjects
            .Find(item => item.TenantId == Data.TenantId)
            .SingleAsync();
        var cleanupRequired = Assert.IsType<CleanupRequiredRegistryProjectProvisioning>(pending.State);
        Assert.Equal("17", cleanupRequired.ExternalProjectId);
        Assert.Equal("21", cleanupRequired.BuildExternalRobotId);
        Assert.Equal("22", cleanupRequired.PullExternalRobotId);
        Assert.NotNull(cleanupRequired.LastCleanupFailureCode);
        Assert.Equal(1, cleanupRequired.CleanupAttemptCount);
        Assert.NotNull(cleanupRequired.LastCleanupAttemptAtUtc);
        Assert.Equal(0, registryClient.DeleteProjectCalls);

        registryClient.FailRobotCleanup = false;
        cleanupService = CreateCleanupService(
            registryClient,
            new FixedTimeProvider(
                new DateTimeOffset(cleanupRequired.CleanupNotBeforeUtc.AddSeconds(1), TimeSpan.Zero)));
        await cleanupService.ReconcileAsync();

        var reconciled = await Fixture.DbContext.RegistryProjects.Find(item => item.Id == pending.Id).SingleAsync();
        Assert.IsType<FailedRegistryProjectProvisioning>(reconciled.State);
        Assert.Equal(2, registryClient.RobotCleanupCalls);
        Assert.Equal(0, registryClient.DeleteProjectCalls);
    }

    [Fact]
    public async Task CleanupClaimCannotReplaceReadyStateOrDeleteReadyCredentials()
    {
        var registryClient = new FakeContainerRegistryClient();
        var cleanupService = CreateCleanupService(registryClient);
        var now = DateTime.UtcNow;
        var project = RegistryProjectFactory.Create(
                Data.TenantId,
                ContainerRegistryProvider.Harbor,
                "pole-ready",
                Data.UserId,
                new RegistryProjectFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now)) with
            {
                State = new ReadyRegistryProject(
                    "17",
                    ObjectId.GenerateNewId().ToString(),
                    ObjectId.GenerateNewId().ToString(),
                    now),
            };
        await Fixture.DbContext.RegistryProjects.InsertOneAsync(project);

        var claimed = await cleanupService.ClaimCleanupAsync(
            project.Id,
            new RegistryProvisioningAttempt(
                AttemptIdOne,
                "pole-ready",
                "17",
                $"pole-build-{AttemptIdOne}",
                $"pole-runtime-{AttemptIdOne}",
                "21",
                "22",
                null),
            new RegistryProvisioningFailure("test.unknown-commit", "Test reconciliation."));

        Assert.Null(claimed);
        Assert.Equal(0, registryClient.RobotCleanupCalls);
        var stored = await Fixture.DbContext.RegistryProjects.Find(item => item.Id == project.Id).SingleAsync();
        Assert.IsType<ReadyRegistryProject>(stored.State);
    }

    [Fact]
    public async Task NameOnlyCleanupClaimCannotReplaceRemoteStateWithKnownRobotIds()
    {
        var now = DateTime.UtcNow;
        var registryClient = new FakeContainerRegistryClient();
        var cleanupService = CreateCleanupService(registryClient);
        var project = RegistryProjectFactory.Create(
                Data.TenantId,
                ContainerRegistryProvider.Harbor,
                "pole-remote",
                Data.UserId,
                new RegistryProjectFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now)) with
            {
                State = new RemoteProvisionedRegistryProject(AttemptIdOne, "17", "21", "22", now),
            };
        await Fixture.DbContext.RegistryProjects.InsertOneAsync(project);
        var names = RegistryRobotNames.ForAttempt(AttemptIdOne);

        var claimed = await cleanupService.ClaimCleanupAsync(
            project.Id,
            new RegistryProvisioningAttempt(
                AttemptIdOne,
                project.Name,
                project.Name,
                names.Build,
                names.Pull,
                null,
                null,
                now + TimeSpan.FromMinutes(10)),
            new RegistryProvisioningFailure("test.stale-provisioning", "Test reconciliation."));

        Assert.Null(claimed);
        Assert.Equal(0, registryClient.RobotCleanupCalls);
        var stored = await Fixture.DbContext.RegistryProjects.Find(item => item.Id == project.Id).SingleAsync();
        var remote = Assert.IsType<RemoteProvisionedRegistryProject>(stored.State);
        Assert.Equal("21", remote.BuildExternalRobotId);
        Assert.Equal("22", remote.PullExternalRobotId);
    }

    [Fact]
    public async Task ReconcilerCleansAbandonedRemoteAttemptByAttemptId()
    {
        var now = DateTimeOffset.UtcNow;
        var registryClient = new FakeContainerRegistryClient();
        var cleanupService = CreateCleanupService(registryClient, new FixedTimeProvider(now));
        var startedAt = now.AddMinutes(-20).UtcDateTime;
        var project = RegistryProjectFactory.Create(
                Data.TenantId,
                ContainerRegistryProvider.Harbor,
                "pole-abandoned",
                Data.UserId,
                new RegistryProjectFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), startedAt)) with
            {
                State = new RemoteProvisionedRegistryProject(AttemptIdOne, "17", "21", "22", startedAt),
                UpdatedAtUtc = startedAt,
            };
        await Fixture.DbContext.RegistryProjects.InsertOneAsync(project);

        await cleanupService.ReconcileAsync();

        Assert.Equal(1, registryClient.RobotCleanupCalls);
        Assert.Equal(["21", "22"], registryClient.DeletedRobotIds);
        Assert.Equal(0, registryClient.DeleteProjectCalls);
        var stored = await Fixture.DbContext.RegistryProjects.Find(item => item.Id == project.Id).SingleAsync();
        Assert.IsType<FailedRegistryProjectProvisioning>(stored.State);
    }

    [Fact]
    public async Task ReconcilerCleansStaleProvisioningByDeterministicNamesWithoutRobotIds()
    {
        var now = DateTimeOffset.UtcNow;
        var registryClient = new FakeContainerRegistryClient();
        var cleanupService = CreateCleanupService(registryClient, new FixedTimeProvider(now));
        var startedAt = now.AddMinutes(-20).UtcDateTime;
        var project = RegistryProjectFactory.Create(
                Data.TenantId,
                ContainerRegistryProvider.Harbor,
                "pole-response-lost",
                Data.UserId,
                new RegistryProjectFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), startedAt)) with
            {
                State = new ProvisioningRegistryProject(AttemptIdOne, startedAt),
                UpdatedAtUtc = startedAt,
            };
        await Fixture.DbContext.RegistryProjects.InsertOneAsync(project);

        await cleanupService.ReconcileAsync();

        var names = RegistryRobotNames.ForAttempt(AttemptIdOne);
        Assert.Equal(1, registryClient.RobotCleanupCalls);
        Assert.Equal([names.Build, names.Pull], registryClient.DeletedRobotNames);
        Assert.Empty(registryClient.DeletedRobotIds);
        var stored = await Fixture.DbContext.RegistryProjects.Find(item => item.Id == project.Id).SingleAsync();
        Assert.IsType<FailedRegistryProjectProvisioning>(stored.State);
    }

    [Fact]
    public async Task ConcurrentCleanupWorkersAreSerializedByDurableLease()
    {
        var now = DateTimeOffset.UtcNow;
        var registryClient = new FakeContainerRegistryClient
        {
            CleanupStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueCleanup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        registryClient.MatchedByNameCounts.Enqueue(1);
        registryClient.MatchedByNameCounts.Enqueue(0);
        var cleanupService = CreateCleanupService(registryClient, new FixedTimeProvider(now));
        var project = CreateCleanupRequiredProject(
            Data.TenantId,
            "pole-concurrent-cleanup",
            AttemptIdOne,
            "17",
            null,
            null,
            now.AddMinutes(-20).UtcDateTime);
        var cleanup = Assert.IsType<CleanupRequiredRegistryProjectProvisioning>(project.State) with
        {
            CleanupVerificationNotBeforeUtc = now.AddMinutes(-1).UtcDateTime,
        };
        project = project with { State = cleanup };
        await Fixture.DbContext.RegistryProjects.InsertOneAsync(project);

        var firstWorker = cleanupService.CleanupAsync(project.Id, cleanup);
        await registryClient.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var staleWorker = await cleanupService.CleanupAsync(project.Id, cleanup);

        Assert.IsType<RegistryCleanupCompleted>(staleWorker.Value);
        Assert.Equal(1, registryClient.RobotCleanupCalls);
        registryClient.ContinueCleanup.SetResult(true);
        Assert.IsType<RegistryCleanupCompleted>((await firstWorker).Value);

        var staleWorkerAfterLeaseRelease = await cleanupService.CleanupAsync(project.Id, cleanup);

        Assert.IsType<RegistryCleanupCompleted>(staleWorkerAfterLeaseRelease.Value);

        var stored = await Fixture.DbContext.RegistryProjects.Find(item => item.Id == project.Id).SingleAsync();
        var pending = Assert.IsType<CleanupRequiredRegistryProjectProvisioning>(stored.State);
        Assert.Null(pending.CleanupLeaseToken);
        Assert.Null(pending.CleanupLeaseExpiresAtUtc);
        Assert.True(pending.CleanupVerificationNotBeforeUtc > now.UtcDateTime);
        Assert.Equal(1, registryClient.RobotCleanupCalls);
    }

    [Fact]
    public async Task FailedCleanupDefersStaleWorkerUntilRetryDelay()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var registryClient = new FakeContainerRegistryClient { FailRobotCleanup = true };
        var cleanupService = CreateCleanupService(registryClient, new FixedTimeProvider(now));
        var project = CreateCleanupRequiredProject(
            Data.TenantId,
            "pole-failed-cleanup-retry",
            AttemptIdOne,
            "17",
            null,
            null,
            now.AddMinutes(-20).UtcDateTime);
        var cleanup = Assert.IsType<CleanupRequiredRegistryProjectProvisioning>(project.State) with
        {
            CleanupVerificationNotBeforeUtc = now.AddMinutes(-1).UtcDateTime,
        };
        project = project with { State = cleanup };
        await Fixture.DbContext.RegistryProjects.InsertOneAsync(project);

        var failedWorker = await cleanupService.CleanupAsync(project.Id, cleanup);
        var staleWorkerAfterLeaseRelease = await cleanupService.CleanupAsync(project.Id, cleanup);

        Assert.IsType<ContainerRegistryProviderUnavailable>(
            Assert.IsType<ContainerRegistryProviderError>(failedWorker.Value).Value);
        Assert.IsType<RegistryCleanupCompleted>(staleWorkerAfterLeaseRelease.Value);
        Assert.Equal(1, registryClient.RobotCleanupCalls);
        var stored = await Fixture.DbContext.RegistryProjects.Find(item => item.Id == project.Id).SingleAsync();
        var pending = Assert.IsType<CleanupRequiredRegistryProjectProvisioning>(stored.State);
        Assert.Equal(now.AddMinutes(1).UtcDateTime, pending.CleanupNotBeforeUtc);
        Assert.True(pending.CleanupVerificationNotBeforeUtc >= pending.CleanupNotBeforeUtc);
        Assert.Null(pending.CleanupLeaseToken);
        Assert.Null(pending.CleanupLeaseExpiresAtUtc);
    }

    [Fact]
    public async Task ExpiredCleanupLeaseIsRecoveredAfterWorkerCrash()
    {
        var now = DateTimeOffset.UtcNow;
        var registryClient = new FakeContainerRegistryClient();
        var cleanupService = CreateCleanupService(registryClient, new FixedTimeProvider(now));
        var project = CreateCleanupRequiredProject(
            Data.TenantId,
            "pole-expired-cleanup-lease",
            AttemptIdOne,
            "17",
            "21",
            "22",
            now.AddMinutes(-10).UtcDateTime);
        var cleanup = Assert.IsType<CleanupRequiredRegistryProjectProvisioning>(project.State) with
        {
            CleanupLeaseToken = "abandoned-lease",
            CleanupLeaseExpiresAtUtc = now.AddMinutes(-1).UtcDateTime,
        };
        project = project with { State = cleanup };
        await Fixture.DbContext.RegistryProjects.InsertOneAsync(project);

        var result = await cleanupService.CleanupAsync(project.Id, cleanup);

        Assert.IsType<RegistryCleanupCompleted>(result.Value);
        Assert.Equal(1, registryClient.RobotCleanupCalls);
        var stored = await Fixture.DbContext.RegistryProjects.Find(item => item.Id == project.Id).SingleAsync();
        Assert.IsType<FailedRegistryProjectProvisioning>(stored.State);
    }

    [Fact]
    public async Task ReconcilerContinuesAfterOneCleanupThrows()
    {
        var now = DateTimeOffset.UtcNow;
        var registryClient = new FakeContainerRegistryClient { ThrowRobotCleanupCalls = 1 };
        var cleanupService = CreateCleanupService(registryClient, new FixedTimeProvider(now));
        var projects = new[]
        {
            CreateCleanupRequiredProject(
                "tenant-cleanup-1",
                "pole-cleanup-1",
                AttemptIdOne,
                "17",
                "21",
                "22",
                now.AddMinutes(-2).UtcDateTime),
            CreateCleanupRequiredProject(
                "tenant-cleanup-2",
                "pole-cleanup-2",
                AttemptIdTwo,
                "18",
                "23",
                "24",
                now.AddMinutes(-1).UtcDateTime),
        };
        await Fixture.DbContext.RegistryProjects.InsertManyAsync(projects);

        await cleanupService.ReconcileAsync();

        Assert.Equal(2, registryClient.RobotCleanupCalls);
        var stored = await Fixture.DbContext
            .RegistryProjects
            .Find(item => projects.Select(project => project.Id).Contains(item.Id))
            .SortBy(item => item.UpdatedAtUtc)
            .ToListAsync();
        Assert.Contains(stored, project => project.State is CleanupRequiredRegistryProjectProvisioning);
        Assert.Contains(stored, project => project.State is FailedRegistryProjectProvisioning);
    }

    [Fact]
    public async Task CredentialRewrapEndpointRequiresPlatformAdminAndExactActiveKey()
    {
        var body = new RewrapRegistryCredentialsRequestBody(
            LocalKeyFileKeyEncryptionProvider.StableProviderId,
            "api-integration",
            1,
            LocalKeyFileKeyEncryptionProvider.Algorithm,
            LocalKeyFileKeyEncryptionProvider.AlgorithmVersion);

        var forbidden = await Client.PostAsJsonAsync(
            "/api/v1/platform/encryption/container-registry-credentials/rewrap",
            body);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var accepted = await Client.PostAsJsonAsync(
            "/api/v1/platform/encryption/container-registry-credentials/rewrap",
            body);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var summary = await accepted.Content.ReadFromJsonAsync<RegistryCredentialRewrapSummary>();
        Assert.NotNull(summary);
        Assert.Equal(0, summary.Examined);
    }

    private static void AssertRawCredentialDoesNotContainPlaintext(BsonDocument credential)
    {
        var rawBson = credential.ToBson().AsSpan();
        foreach (var plaintext in new[]
                 {
                     FakeContainerRegistryClient.BuildSecret,
                     FakeContainerRegistryClient.PullSecret,
                 })
        {
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var encodedPlaintextBytes = Encoding.UTF8.GetBytes(Convert.ToBase64String(plaintextBytes));
            Assert.True(rawBson.IndexOf(plaintextBytes) < 0);
            Assert.True(rawBson.IndexOf(encodedPlaintextBytes) < 0);
        }
    }

    private EnsureTenantContainerRegistryProject CreateProvisioningService(
        IContainerRegistryClient registryClient,
        ProtectRegistryCredentialSecret protectSecret)
    {
        var persistence = CreatePersistence();
        var cleanup = new RegistryProvisioningCleanupService(
            persistence,
            registryClient,
            TimeProvider.System,
            NullLogger<RegistryProvisioningCleanupService>.Instance);
        return new ContainerRegistryProvisioningService(
            persistence,
            registryClient,
            protectSecret,
            cleanup,
            TimeProvider.System).InvokeAsync;
    }

    private RegistryProvisioningCleanupService CreateCleanupService(
        IContainerRegistryClient registryClient,
        TimeProvider? timeProvider = null) => new(
        CreatePersistence(timeProvider),
        registryClient,
        timeProvider ?? TimeProvider.System,
        NullLogger<RegistryProvisioningCleanupService>.Instance);

    private RegistryProvisioningPersistence CreatePersistence(TimeProvider? timeProvider = null) => new(
        Fixture.Database.GetCollection<RegistryProject>("RegistryProjects"),
        Fixture.Database.GetCollection<RegistryCredential>("RegistryCredentials"),
        new MongoRunTransaction(Fixture.Client),
        timeProvider ?? TimeProvider.System);

    private RegistryProject CreateCleanupRequiredProject(
        string tenantId,
        string name,
        string attemptId,
        string externalProjectId,
        string? buildRobotId,
        string? pullRobotId,
        DateTime now)
    {
        var robotNames = RegistryRobotNames.ForAttempt(attemptId);
        return RegistryProjectFactory.Create(
                tenantId,
                ContainerRegistryProvider.Harbor,
                name,
                Data.UserId,
                new RegistryProjectFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now)) with
            {
                State = new CleanupRequiredRegistryProjectProvisioning(
                    attemptId,
                    name,
                    externalProjectId,
                    robotNames.Build,
                    robotNames.Pull,
                    buildRobotId,
                    pullRobotId,
                    nameof(ContainerRegistryProviderUnavailable),
                    "Provisioning failed.",
                    null,
                    null,
                    0,
                    now,
                    now,
                    now,
                    null,
                    null,
                    null),
            };
    }

    private static ValueTask<ProtectRegistryCredentialOutcome> CancelProtectAsync(
        string tenantId,
        string credentialId,
        long secretVersion,
        string secret,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ProtectRegistryCredentialOutcome>(new OperationCanceledException());

    private sealed class FixedTimeProvider(
        DateTimeOffset utcNow
    ) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestEnvelopeEncryptionScope : IDisposable
    {
        private readonly string _directory;
        private readonly LocalKeyFileKeyEncryptionProvider _provider;

        private TestEnvelopeEncryptionScope(
            string directory,
            LocalKeyFileKeyEncryptionProvider provider,
            IEnvelopeEncryptionService service)
        {
            _directory = directory;
            _provider = provider;
            Service = service;
        }

        public IEnvelopeEncryptionService Service { get; }

        public void Dispose()
        {
            _provider.Dispose();
            Directory.Delete(_directory, recursive: true);
        }

        public static TestEnvelopeEncryptionScope Create()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "pole-encryption-integration",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "integration-v1.key");
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                File.WriteAllText(path, Convert.ToBase64String(key));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            var provider = new LocalKeyFileKeyEncryptionProvider(
                Options.Create(
                    new LocalKeyFileOptions
                    {
                        ActiveKeyId = "integration",
                        ActiveKeyVersion = 1,
                        Keys =
                        [
                            new LocalKeyFileKeyOptions { Id = "integration", Version = 1, Path = path },
                        ],
                    }));
            var service = new EnvelopeEncryptionService(
                [provider],
                Options.Create(
                    new EnvelopeEncryptionOptions
                    {
                        ActiveProviderId = LocalKeyFileKeyEncryptionProvider.StableProviderId,
                    }));
            return new TestEnvelopeEncryptionScope(directory, provider, service);
        }
    }

    private sealed class RotationEncryptionScope : IDisposable
    {
        private readonly string _directory;
        private readonly LocalKeyFileKeyEncryptionProvider[] _providers;

        private RotationEncryptionScope(
            string directory,
            LocalKeyFileKeyEncryptionProvider[] providers,
            IEnvelopeEncryptionService oldService,
            IEnvelopeEncryptionService rotatingService,
            IEnvelopeEncryptionService newOnlyService)
        {
            _directory = directory;
            _providers = providers;
            OldService = oldService;
            RotatingService = rotatingService;
            NewOnlyService = newOnlyService;
        }

        public IEnvelopeEncryptionService OldService { get; }
        public IEnvelopeEncryptionService RotatingService { get; }
        public IEnvelopeEncryptionService NewOnlyService { get; }

        public void Dispose()
        {
            foreach (var provider in _providers)
                provider.Dispose();
            Directory.Delete(_directory, recursive: true);
        }

        public static RotationEncryptionScope Create()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "pole-encryption-rotation-integration",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var oldPath = WriteKey(directory, "old-v1.key");
            var newPath = WriteKey(directory, "new-v2.key");
            var oldProvider = CreateProvider("old", 1, ("old", 1, oldPath));
            var rotatingProvider = CreateProvider("new", 2, ("old", 1, oldPath), ("new", 2, newPath));
            var newOnlyProvider = CreateProvider("new", 2, ("new", 2, newPath));
            return new RotationEncryptionScope(
                directory,
                [oldProvider, rotatingProvider, newOnlyProvider],
                CreateService(oldProvider),
                CreateService(rotatingProvider),
                CreateService(newOnlyProvider));
        }

        private static string WriteKey(string directory, string fileName)
        {
            var path = Path.Combine(directory, fileName);
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                File.WriteAllText(path, Convert.ToBase64String(key));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            return path;
        }

        private static LocalKeyFileKeyEncryptionProvider CreateProvider(
            string activeId,
            int activeVersion,
            params (string Id, int Version, string Path)[] keys) => new(
            Options.Create(
                new LocalKeyFileOptions
                {
                    ActiveKeyId = activeId,
                    ActiveKeyVersion = activeVersion,
                    Keys = keys.Select(key => new LocalKeyFileKeyOptions
                        {
                            Id = key.Id,
                            Version = key.Version,
                            Path = key.Path,
                        })
                        .ToList(),
                }));

        private static IEnvelopeEncryptionService CreateService(LocalKeyFileKeyEncryptionProvider provider) =>
            new EnvelopeEncryptionService(
                [provider],
                Options.Create(
                    new EnvelopeEncryptionOptions
                    {
                        ActiveProviderId = LocalKeyFileKeyEncryptionProvider.StableProviderId,
                    }));
    }

    private sealed class FakeContainerRegistryClient : IContainerRegistryClient
    {
        public const string BuildSecret = "raw-build-secret";
        public const string PullSecret = "raw-pull-secret";

        public int ProvisionCalls { get; private set; }
        public int RobotCleanupCalls { get; private set; }
        public int DeleteProjectCalls { get; private set; }
        public List<string> DeletedRobotIds { get; } = [];
        public List<string> DeletedRobotNames { get; } = [];
        public Queue<int> MatchedByNameCounts { get; } = [];
        public bool FailNextProvision { get; init; }
        public bool FailRobotCleanup { get; set; }
        public int ThrowRobotCleanupCalls { get; set; }
        public TaskCompletionSource<bool>? CleanupStarted { get; init; }
        public TaskCompletionSource<bool>? ContinueCleanup { get; init; }

        public Task<ProvisionRegistryProjectOutcome> ProvisionProjectAsync(
            RegistryProjectProvisioningRequest request,
            CancellationToken cancellationToken = default)
        {
            ProvisionCalls++;
            if (FailNextProvision && ProvisionCalls == 1)
                return Task.FromResult<ProvisionRegistryProjectOutcome>(
                    new ContainerRegistryProviderError(new ContainerRegistryProviderUnavailable("Harbor")));

            return Task.FromResult<ProvisionRegistryProjectOutcome>(
                new ProvisionedRegistryProject(
                    "17",
                    request.ProjectName,
                    new RegistryRobotCredential("21", "robot$build", BuildSecret, null),
                    new RegistryRobotCredential("22", "robot$runtime", PullSecret, null)));
        }

        public Task<GetRegistryArtifactOutcome> GetArtifactAsync(
            GetRegistryArtifactRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult<GetRegistryArtifactOutcome>(
            new ContainerRegistryArtifactLookupError(new ContainerRegistryArtifactNotFound(request.Reference)));

        public async Task<DeleteRegistryRobotCredentialsOutcome> DeleteRobotCredentialsAsync(
            IReadOnlyCollection<RegistryRobotCleanupTarget> targets,
            CancellationToken cancellationToken = default)
        {
            RobotCleanupCalls++;
            DeletedRobotNames.AddRange(targets.Select(target => target.Name));
            DeletedRobotIds.AddRange(
                targets.Select(target => target.ExternalRobotId).Where(id => id is not null).Select(id => id!));
            if (ThrowRobotCleanupCalls > 0)
            {
                ThrowRobotCleanupCalls--;
                throw new HttpRequestException("simulated cleanup transport failure");
            }

            CleanupStarted?.TrySetResult(true);
            if (ContinueCleanup is not null)
                await ContinueCleanup.Task.WaitAsync(cancellationToken);
            return FailRobotCleanup
                ? new ContainerRegistryProviderError(new ContainerRegistryProviderUnavailable("Harbor"))
                : new RegistryRobotCleanupResult(
                    MatchedByNameCounts.TryDequeue(out var matchedCount) ? matchedCount : 0);
        }

        public Task<DeleteRegistryProjectOutcome> DeleteProjectAsync(
            string externalProjectId,
            CancellationToken cancellationToken = default)
        {
            DeleteProjectCalls++;
            return Task.FromResult<DeleteRegistryProjectOutcome>(new RegistryProjectDeleted());
        }
    }
}
