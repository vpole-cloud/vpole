// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using MongoDB.Bson;
using Microsoft.Extensions.Options;
using Pole.Features.Tenants.Events;
using Pole.Features.Tenants.Migrations;
using Pole.Features.Events.Abstractions;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application;
using Pole.Features.Tenants.Application.Invitations;
using Pole.Features.Tenants.Application.Members;
using Pole.Features.Tenants.Domain;
using Pole.Features.Users.Domain;
using Pole.Features.Users.Application;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Tenants;

[Collection(IntegrationTestCollection.Global2)]
public sealed class TenantInvitationTransactionTests(
    MongoReplicaSetFixture fixture
) : IsolatedMediatorIntegrationTestBase(fixture, IntegrationSchemaProfile.Identity)
{
    private readonly List<InvitationMail> _delivered = [];
    private bool _mailFails;
    private bool _outboxFails;
    private SendInvitationMail? _sendOverride;

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(Options.Create(new InvitationOptions { FrontendUrl = "https://console.example.test" }));
        services.AddSingleton<SendInvitationMail>((mail, ct) =>
        {
            if (_sendOverride is not null)
                return _sendOverride(mail, ct);
            if (_mailFails)
                throw new System.Net.Mail.SmtpException("test mail transport failed");
            _delivered.Add(mail);
            return Task.CompletedTask;
        });
        services.AddSingleton<StoreOutboxWrites>(async (
            session, writes,
            ct) =>
        {
            await Fixture.StoreOutboxWrites(session, writes, ct);
            if (_outboxFails)
                throw new InvalidOperationException("test outbox failed");
        });
    }

    private IMongoCollection<TenantInvitation> Invitations =>
        DbContext.Database.GetCollection<TenantInvitation>("TenantInvitations");

    [Fact]
    public async Task InvitationAndAcceptanceCreateOnlyTheInvitedTenantMembershipAndOneReceipt()
    {
        var tenant = await CreateTenant();
        var tenantCount = await DbContext.Tenants.CountDocumentsAsync(FilterDefinition<Tenant>.Empty);
        var token = await Invite(tenant, "New@Example.test");
        var accept = Services.GetRequiredService<PersistAcceptedTenantInvitation>();
        var request = new AcceptTenantInvitation(token, "new-subject", "new@example.test", null, null);
        var accepted = Assert.IsType<InvitationAcceptedDto>((await accept(request)).Value);
        Assert.Equal(tenant.Id, accepted.TenantId);
        Assert.Equal(tenant.Id, accepted.DefaultTenantId);
        Assert.IsType<InvitationAcceptedDto>((await accept(request)).Value);
        var stored = await DbContext.Tenants.Find(item => item.Id == tenant.Id).SingleAsync();
        var member = Assert.Single(stored.Members, item => item.UserId == "new-subject");
        Assert.Equal(TenantRole.Viewer, member.Role);
        Assert.True(member.IsDefault);
        Assert.Equal(
            tenant.Id,
            (await DbContext.Users.Find(item => item.Id == "new-subject").SingleAsync()).DefaultTenantId);
        Assert.Equal(tenantCount, await DbContext.Tenants.CountDocumentsAsync(FilterDefinition<Tenant>.Empty));
        Assert.Equal(
            1,
            await DbContext.Database
                .GetCollection<TenantInvitationAcceptance>("TenantInvitationAcceptances")
                .CountDocumentsAsync(FilterDefinition<TenantInvitationAcceptance>.Empty));
        Assert.Equal(1, await DbContext.CountAllOutboxMessagesAsync("pole.tenants/v1/membership-changed"));
        Assert.Equal(0, await DbContext.CountAllOutboxMessagesAsync("pole.users/v1/user-created"));
    }

    [Fact]
    public async Task IssuerDowngradeAndWrongRecipientCannotGrantMembership()
    {
        var tenant = await CreateTenant();
        var token = await Invite(tenant);
        var accept = Services.GetRequiredService<PersistAcceptedTenantInvitation>();
        Assert.IsType<InvitationIdentityMismatch>(
            (await accept(new AcceptTenantInvitation(token, "recipient", "other@example.test", null, null))).Value);
        var issuer = tenant.Members[0];
        var replacement = TenantFactory.CreateMember(
            tenant.Id,
            "replacement",
            "replacement@example.test",
            TenantRole.Owner);
        await DbContext.Tenants.ReplaceOneAsync(
            item => item.Id == tenant.Id,
            tenant with { Members = [issuer with { Role = TenantRole.Viewer }, replacement] });
        Assert.IsType<InvitationUnavailable>(
            (await accept(new AcceptTenantInvitation(token, "recipient", "new@example.test", null, null))).Value);
        Assert.False(await DbContext.Users.Find(item => item.Id == "recipient").AnyAsync());
    }

    [Theory]
    [InlineData(TenantRole.Viewer, TenantRole.Viewer, false)]
    [InlineData(TenantRole.Member, TenantRole.Viewer, false)]
    [InlineData(TenantRole.Admin, TenantRole.Admin, true)]
    [InlineData(TenantRole.Admin, TenantRole.Owner, false)]
    [InlineData(TenantRole.Owner, TenantRole.Owner, true)]
    public async Task GrantAuthorityUsesLiveTenantRole(
        TenantRole issuerRole,
        TenantRole grantRole,
        bool allowed)
    {
        var tenant = await CreateTenant(issuerRole);
        var result = await Services.GetRequiredService<PersistTenantInvitation>()(
            new CreateTenantInvitation(tenant.Id, "new@example.test", grantRole, new InvitationActor("issuer")));
        Assert.Equal(allowed, result.Value is InvitationDto);
        if (!allowed)
            Assert.IsType<InvitationForbidden>(result.Value);
    }

    [Fact]
    public async Task ConcurrentFirstJoinsChooseOneDefaultAndParallelAcceptsDoNotDuplicateMembership()
    {
        var firstTenant = await CreateTenant();
        var secondTenant = await CreateTenant();
        var first = await Invite(firstTenant);
        var second = await Invite(secondTenant);
        using var firstScope = Services.CreateScope();
        using var secondScope = Services.CreateScope();
        var results = await Task.WhenAll(
            firstScope.ServiceProvider.GetRequiredService<PersistAcceptedTenantInvitation>()(
                new AcceptTenantInvitation(first, "recipient", "new@example.test", null, null)),
            secondScope.ServiceProvider.GetRequiredService<PersistAcceptedTenantInvitation>()(
                new AcceptTenantInvitation(second, "recipient", "new@example.test", null, null)));
        Assert.All(results, result => Assert.IsType<InvitationAcceptedDto>(result.Value));
        var tenants = await DbContext.Tenants
            .Find(item => item.Members.Any(member => member.UserId == "recipient"))
            .ToListAsync();
        Assert.Equal(2, tenants.Count);
        Assert.Single(
            tenants.SelectMany(item => item.Members),
            member => member.UserId == "recipient" && member.IsDefault);
        var user = await DbContext.Users.Find(item => item.Id == "recipient").SingleAsync();
        Assert.All(
            results,
            result => Assert.Equal(user.DefaultTenantId, ((InvitationAcceptedDto)result.Value).DefaultTenantId));
        Assert.Equal(2, await DbContext.CountAllOutboxMessagesAsync("pole.tenants/v1/membership-changed"));
    }

    [Fact]
    public async Task AcceptedTokenRetryAfterRemovalNeverRestoresAccess()
    {
        var tenant = await CreateTenant();
        var token = await Invite(tenant);
        var request = new AcceptTenantInvitation(token, "recipient", "new@example.test", null, null);
        var accept = Services.GetRequiredService<PersistAcceptedTenantInvitation>();
        await accept(request);
        var member = (await DbContext.Tenants.Find(item => item.Id == tenant.Id).SingleAsync()).Members.Single(item =>
            item.UserId == "recipient");
        await Services.GetRequiredService<PersistRemovedTenantMember>()(
            new RemoveTenantMember(tenant.Id, member.Id, new TenantMemberActor("test-admin", true)));
        Assert.IsType<InvitationUnavailable>((await accept(request)).Value);
        Assert.DoesNotContain(
            (await DbContext.Tenants.Find(item => item.Id == tenant.Id).SingleAsync()).Members,
            item => item.UserId == "recipient");
    }

    [Fact]
    public async Task AcceptanceOutboxFailureRollsBackUserReceiptAndMembershipTogether()
    {
        var tenant = await CreateTenant();
        var token = await Invite(tenant);
        var services = new ServiceCollection();
        services.AddSingleton(DbContext.Database);
        services.AddSingleton(DbContext.Database.Client);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<MongoRunTransaction>();
        services.AddSingleton<StoreOutboxWrites>((
            _, _,
            _) => throw new InvalidOperationException("outbox unavailable"));
        services.AddTenantsFeature();
        services.AddUsersFeature(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<PersistAcceptedTenantInvitation>()(
                new AcceptTenantInvitation(token, "recipient", "new@example.test", null, null)));
        Assert.False(await DbContext.Users.Find(item => item.Id == "recipient").AnyAsync());
        Assert.Equal(
            TenantInvitationStatus.Pending,
            (await Invitations.Find(item => item.TenantId == tenant.Id).SingleAsync()).Status);
        Assert.DoesNotContain(
            (await DbContext.Tenants.Find(item => item.Id == tenant.Id).SingleAsync()).Members,
            item => item.UserId == "recipient");
    }

    [Fact]
    public async Task ResendRotatesTheSecretAndStaleDeliveryNeverSends()
    {
        var tenant = await CreateTenant();
        var oldToken = await Invite(tenant);
        var before = await Invitations.Find(item => item.TenantId == tenant.Id).SingleAsync();
        var result = await Services.GetRequiredService<ManageInvitation>()
            .ResendAsync(
                new ManageInvitationRequest(tenant.Id, before.Id, 1, new InvitationActor("issuer")),
                CancellationToken.None);
        Assert.IsType<InvitationDto>(result.Value);
        var current = await Invitations.Find(item => item.Id == before.Id).SingleAsync();
        Assert.NotEqual(before.TokenHash, current.TokenHash);
        Assert.Equal(before.Revision + 1, current.Revision);
        Assert.True(current.ExpiresAtUtc >= before.ExpiresAtUtc);
        var delivery = Services.GetRequiredService<DeliverTenantInvitation>();
        await delivery.InvokeAsync(
            new TenantInvitationDeliveryRequested(before.Id, before.Revision, EventCore.Create(tenant.Id)),
            CancellationToken.None);
        Assert.Empty(_delivered);
        var currentEvent = new TenantInvitationDeliveryRequested(
            current.Id,
            current.Revision,
            EventCore.Create(tenant.Id));
        await delivery.InvokeAsync(currentEvent, CancellationToken.None);
        await delivery.InvokeAsync(currentEvent, CancellationToken.None);
        var mail = Assert.Single(_delivered);
        Assert.StartsWith("https://console.example.test/invitations#token=", mail.AcceptanceUrl);
        Assert.DoesNotContain(oldToken, mail.AcceptanceUrl);
        Assert.Equal(current.ExpiresAtUtc, mail.ExpiresAtUtc);
        Assert.Equal(
            InvitationDeliveryStatus.Sent,
            (await Invitations.Find(item => item.Id == current.Id).SingleAsync()).DeliveryStatus);
        Assert.IsType<InvitationUnavailable>(
            (await Services.GetRequiredService<PersistAcceptedTenantInvitation>()(
                new AcceptTenantInvitation(oldToken, "recipient", "new@example.test", null, null))).Value);
    }

    [Fact]
    public async Task DelayedDeliveryUsesPersistedExpiry()
    {
        var tenant = await CreateTenant();
        await Invite(tenant);
        var invitation = await Invitations.Find(item => item.TenantId == tenant.Id).SingleAsync();
        var expiresAt = DateTime.UtcNow.AddHours(2);
        // A queued delivery has only the persisted remaining lifetime, not another seven days.
        await Invitations.UpdateOneAsync(
            item => item.Id == invitation.Id,
            Builders<TenantInvitation>.Update.Set(item => item.ExpiresAtUtc, expiresAt));
        var persisted = await Invitations.Find(item => item.Id == invitation.Id).SingleAsync();
        await Services.GetRequiredService<DeliverTenantInvitation>()
            .InvokeAsync(
                new TenantInvitationDeliveryRequested(invitation.Id, invitation.Revision, EventCore.Create(tenant.Id)),
                CancellationToken.None);
        Assert.Equal(persisted.ExpiresAtUtc, Assert.Single(_delivered).ExpiresAtUtc);
    }

    [Fact]
    public async Task RevokedExpiredAndWrongTenantDeliveriesCannotSendOrGrant()
    {
        var tenant = await CreateTenant();
        var token = await Invite(tenant);
        var invitation = await Invitations.Find(item => item.TenantId == tenant.Id).SingleAsync();
        var delivery = Services.GetRequiredService<DeliverTenantInvitation>();
        await delivery.InvokeAsync(
            new TenantInvitationDeliveryRequested(
                invitation.Id,
                invitation.Revision,
                EventCore.Create(TenantFactory.Create("wrong").Id)),
            CancellationToken.None);
        Assert.Empty(_delivered);
        await Services.GetRequiredService<ManageInvitation>()
            .RevokeAsync(
                new ManageInvitationRequest(tenant.Id, invitation.Id, 1, new InvitationActor("issuer")),
                CancellationToken.None);
        await delivery.InvokeAsync(
            new TenantInvitationDeliveryRequested(invitation.Id, invitation.Revision, EventCore.Create(tenant.Id)),
            CancellationToken.None);
        Assert.Empty(_delivered);
        var accept = Services.GetRequiredService<PersistAcceptedTenantInvitation>();
        Assert.IsType<InvitationUnavailable>(
            (await accept(new AcceptTenantInvitation(token, "recipient", "new@example.test", null, null))).Value);
        var fresh = await Invite(tenant);
        invitation = await Invitations.Find(item => item.Id == invitation.Id).SingleAsync();
        await Invitations.UpdateOneAsync(
            item => item.Id == invitation.Id,
            Builders<TenantInvitation>.Update.Set(item => item.ExpiresAtUtc, DateTime.UtcNow.AddSeconds(-1)));
        await delivery.InvokeAsync(
            new TenantInvitationDeliveryRequested(invitation.Id, invitation.Revision, EventCore.Create(tenant.Id)),
            CancellationToken.None);
        Assert.Empty(_delivered);
        Assert.IsType<InvitationUnavailable>(
            (await accept(new AcceptTenantInvitation(fresh, "recipient", "new@example.test", null, null))).Value);
    }

    [Fact]
    public async Task MailFailureIsPersistedAndRetryUsesTheCurrentRevision()
    {
        var tenant = await CreateTenant();
        await Invite(tenant);
        var invitation = await Invitations.Find(item => item.TenantId == tenant.Id).SingleAsync();
        var delivery = Services.GetRequiredService<DeliverTenantInvitation>();
        var message = new TenantInvitationDeliveryRequested(
            invitation.Id,
            invitation.Revision,
            EventCore.Create(tenant.Id));
        _mailFails = true;
        await Assert.ThrowsAsync<System.Net.Mail.SmtpException>(() => delivery.InvokeAsync(
            message,
            CancellationToken.None));
        Assert.Equal(
            InvitationDeliveryStatus.Failed,
            (await Invitations.Find(item => item.Id == invitation.Id).SingleAsync()).DeliveryStatus);
        _mailFails = false;
        await delivery.InvokeAsync(message, CancellationToken.None);
        Assert.Single(_delivered);
        Assert.Equal(
            InvitationDeliveryStatus.Sent,
            (await Invitations.Find(item => item.Id == invitation.Id).SingleAsync()).DeliveryStatus);
    }

    [Fact]
    public async Task InvitationOutboxFailureRollsBackCreationResendAndRevocation()
    {
        var tenant = await CreateTenant();
        _outboxFails = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Services.GetRequiredService<PersistTenantInvitation>()(
                new CreateTenantInvitation(
                    tenant.Id,
                    "new@example.test",
                    TenantRole.Viewer,
                    new InvitationActor("issuer"))));
        Assert.False(await Invitations.Find(item => item.TenantId == tenant.Id).AnyAsync());
        _outboxFails = false;
        await Invite(tenant);
        var before = await Invitations.Find(item => item.TenantId == tenant.Id).SingleAsync();
        var request = new ManageInvitationRequest(tenant.Id, before.Id, 1, new InvitationActor("issuer"));
        _outboxFails = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Services.GetRequiredService<ManageInvitation>().ResendAsync(request, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Services.GetRequiredService<ManageInvitation>().RevokeAsync(request, CancellationToken.None));
        var after = await Invitations.Find(item => item.Id == before.Id).SingleAsync();
        Assert.Equal(before.TokenHash, after.TokenHash);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(TenantInvitationStatus.Pending, after.Status);
    }

    [Fact]
    public async Task ZeroMembershipReentrySelectsTheNewDefaultAndUsedLinkStaysUnavailable()
    {
        var first = await CreateTenant();
        var firstToken = await Invite(first);
        var accept = Services.GetRequiredService<PersistAcceptedTenantInvitation>();
        await accept(new AcceptTenantInvitation(firstToken, "recipient", "new@example.test", null, null));
        var member = (await DbContext.Tenants.Find(item => item.Id == first.Id).SingleAsync()).Members.Single(item =>
            item.UserId == "recipient");
        await Services.GetRequiredService<PersistRemovedTenantMember>()(
            new RemoveTenantMember(first.Id, member.Id, new TenantMemberActor("issuer", false)));
        var user = await DbContext.Users.Find(item => item.Id == "recipient").SingleAsync();
        Assert.Equal("", user.DefaultTenantId);
        var next = await CreateTenant();
        var nextToken = await Invite(next);
        var joined = Assert.IsType<InvitationAcceptedDto>(
            (await accept(new AcceptTenantInvitation(nextToken, "recipient", "new@example.test", null, null))).Value);
        Assert.Equal(next.Id, joined.DefaultTenantId);
        Assert.Equal(
            next.Id,
            (await DbContext.Users.Find(item => item.Id == "recipient").SingleAsync()).DefaultTenantId);
        Assert.IsType<InvitationUnavailable>(
            (await accept(new AcceptTenantInvitation(firstToken, "recipient", "new@example.test", null, null))).Value);
    }

    [Fact]
    public async Task MigrationIsRepeatableAndEnforcesActualBsonShapeAndIdentityIndexes()
    {
        var migration = new TenantInvitationIndexesMigration();
        await migration.Up(DbContext.Database);
        await migration.Up(DbContext.Database);
        var tenant = await CreateTenant();
        var token = await Invite(tenant);
        var invitation = await Invitations.Find(item => item.TenantId == tenant.Id).SingleAsync();
        var bson = invitation.ToBsonDocument();
        Assert.True(bson["Role"].IsInt32);
        Assert.True(bson["Revision"].IsInt64);
        Assert.True(bson["DeliveryToken"]["Ciphertext"].IsBsonBinaryData);
        Assert.DoesNotContain(token, bson.ToJson());
        var raw = DbContext.Database.GetCollection<BsonDocument>("TenantInvitations");
        var malformed = bson.DeepClone().AsBsonDocument;
        malformed["_id"] = new string('A', 64);
        malformed.Remove("IssuerId");
        var validation = await Assert.ThrowsAsync<MongoWriteException>(() => raw.InsertOneAsync(malformed));
        Assert.Equal(121, validation.WriteError.Code);
        var paddedHash = bson.DeepClone().AsBsonDocument;
        paddedHash["_id"] = new string('A', 64);
        paddedHash["TokenHash"] = new string('C', 64) + "\n";
        validation = await Assert.ThrowsAsync<MongoWriteException>(() => raw.InsertOneAsync(paddedHash));
        Assert.Equal(121, validation.WriteError.Code);
        var duplicate = bson.DeepClone().AsBsonDocument;
        duplicate["_id"] = new string('B', 64);
        duplicate["TokenHash"] = new string('C', 64);
        var unique = await Assert.ThrowsAsync<MongoWriteException>(() => raw.InsertOneAsync(duplicate));
        Assert.Equal(11000, unique.WriteError.Code);
        await Services.GetRequiredService<PersistAcceptedTenantInvitation>()(
            new AcceptTenantInvitation(token, "recipient", "new@example.test", null, null));
        var receipts = DbContext.Database.GetCollection<BsonDocument>("TenantInvitationAcceptances");
        var receipt = await receipts.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        receipt["_id"] = new string('D', 64);
        receipt.Remove("Subject");
        validation = await Assert.ThrowsAsync<MongoWriteException>(() => receipts.InsertOneAsync(receipt));
        Assert.Equal(121, validation.WriteError.Code);
    }

    [Theory]
    [InlineData(TenantRole.Viewer, TenantRole.Member, false)]
    [InlineData(TenantRole.Admin, TenantRole.Owner, false)]
    [InlineData(TenantRole.Admin, TenantRole.Member, true)]
    public async Task MemberCrudRechecksCurrentActorRoleInTheMutationTransaction(
        TenantRole role,
        TenantRole targetRole,
        bool allowed)
    {
        var tenant = await CreateTenant(role);
        var target = TenantFactory.CreateMember(tenant.Id, "target", "target@example.test", targetRole);
        tenant = tenant with { Members = tenant.Members.Add(target) };
        await DbContext.Tenants.ReplaceOneAsync(item => item.Id == tenant.Id, tenant);
        var actor = new TenantMemberActor("issuer", false);
        var result = await Services.GetRequiredService<PersistUpdatedTenantMemberRole>()(
            new UpdateTenantMemberRole(tenant.Id, target.Id, TenantRole.Viewer, actor));
        Assert.Equal(allowed, result.Value is TenantMember);
        if (!allowed)
            Assert.IsType<TenantMemberManagementForbidden>(result.Value);
        var removed = await Services.GetRequiredService<PersistRemovedTenantMember>()(
            new RemoveTenantMember(tenant.Id, target.Id, actor));
        Assert.Equal(allowed, removed.Value is TenantMemberRemoved);
        if (!allowed)
            Assert.IsType<TenantMemberManagementForbidden>(removed.Value);
    }

    [Fact]
    public async Task EquivalentRouteIdCasingUsesOneDecryptableInvitationSlot()
    {
        var tenant = await CreateTenant();
        var persist = Services.GetRequiredService<PersistTenantInvitation>();
        var created = await persist(
            new CreateTenantInvitation(
                tenant.Id.ToUpperInvariant(),
                "new@example.test",
                TenantRole.Viewer,
                new InvitationActor("issuer")));
        Assert.IsType<InvitationDto>(created.Value);
        var stored = await Invitations.Find(item => item.TenantId == tenant.Id).SingleAsync();
        var decrypted = await Services.GetRequiredService<IEnvelopeEncryptionService>()
            .DecryptStringAsync(
                stored.DeliveryToken,
                EnvelopeEncryptionContext.ForTenant(
                    stored.TenantId,
                    "TenantInvitation",
                    stored.Id,
                    "delivery-token",
                    stored.Revision));
        Assert.IsType<string>(decrypted.Value);
        var duplicate = await persist(
            new CreateTenantInvitation(
                tenant.Id,
                "new@example.test",
                TenantRole.Viewer,
                new InvitationActor("issuer")));
        Assert.IsType<InvitationAlreadyPending>(duplicate.Value);
        var listed = await Services.GetRequiredService<MongoLoadInvitation>()
            .ListAsync(tenant.Id.ToUpperInvariant(), new InvitationActor("issuer"), CancellationToken.None);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<InvitationDto>>(listed.Value));
        var revoked = await Services.GetRequiredService<ManageInvitation>()
            .RevokeAsync(
                new ManageInvitationRequest(tenant.Id.ToUpperInvariant(), stored.Id, 1, new InvitationActor("issuer")),
                CancellationToken.None);
        Assert.IsType<InvitationDto>(revoked.Value);
        Assert.Equal(
            TenantInvitationStatus.Revoked,
            (await Invitations.Find(item => item.Id == stored.Id).SingleAsync()).Status);
    }

    [Fact]
    public async Task LateFailedDuplicateCannotReplaceCommittedSentDeliveryStatus()
    {
        var tenant = await CreateTenant();
        await Invite(tenant);
        var invitation = await Invitations.Find(item => item.TenantId == tenant.Id).SingleAsync();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        _sendOverride = async (mail, ct) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                firstStarted.SetResult();
                await releaseFailure.Task.WaitAsync(ct);
                throw new System.Net.Mail.SmtpException("late failed duplicate");
            }

            _delivered.Add(mail);
        };
        var message = new TenantInvitationDeliveryRequested(
            invitation.Id,
            invitation.Revision,
            EventCore.Create(tenant.Id));
        var delivery = Services.GetRequiredService<DeliverTenantInvitation>();
        var failedDuplicate = delivery.InvokeAsync(message, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await delivery.InvokeAsync(message, CancellationToken.None);
        var eventsAfterSent = await DbContext.CountAllOutboxMessagesAsync("pole.tenants/v1/members-changed");
        releaseFailure.SetResult();
        await Assert.ThrowsAsync<System.Net.Mail.SmtpException>(() => failedDuplicate);
        Assert.Single(_delivered);
        Assert.Equal(
            InvitationDeliveryStatus.Sent,
            (await Invitations.Find(item => item.Id == invitation.Id).SingleAsync()).DeliveryStatus);
        Assert.Equal(eventsAfterSent, await DbContext.CountAllOutboxMessagesAsync("pole.tenants/v1/members-changed"));
    }

    private async Task<Tenant> CreateTenant(TenantRole role = TenantRole.Owner)
    {
        var tenant = TenantFactory.Create("Invitation target");
        tenant = tenant with
        {
            Members = [TenantFactory.CreateMember(tenant.Id, "issuer", "issuer@example.test", role)]
        };
        await DbContext.Tenants.InsertOneAsync(tenant);
        return tenant;
    }

    private async Task<string> Invite(Tenant tenant, string email = "new@example.test")
    {
        var outcome = await Services.GetRequiredService<PersistTenantInvitation>()(
            new CreateTenantInvitation(tenant.Id, email, TenantRole.Viewer, new InvitationActor("issuer")));
        var invitation = Assert.IsType<InvitationDto>(outcome.Value);
        var stored = await Invitations.Find(item => item.Id == invitation.Id).SingleAsync();
        var decrypted = await Services.GetRequiredService<IEnvelopeEncryptionService>()
            .DecryptStringAsync(
                stored.DeliveryToken,
                EnvelopeEncryptionContext.ForTenant(
                    stored.TenantId,
                    "TenantInvitation",
                    stored.Id,
                    "delivery-token",
                    stored.Revision));
        var token = Assert.IsType<string>(decrypted.Value);
        var delivery = await DbContext.OutboxMessages
            .Find(item => item.EventType == "pole.tenants/v1/invitation-delivery-requested")
            .ToListAsync();
        Assert.All(delivery, item => Assert.DoesNotContain(token, item.PayloadJson));
        return token;
    }
}
