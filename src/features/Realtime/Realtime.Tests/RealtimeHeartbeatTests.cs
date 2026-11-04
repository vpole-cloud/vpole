using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Pole.Features.Realtime.Application;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Tenants.Contracts;
using Xunit;

namespace Pole.Features.Realtime.Tests;

public sealed class RealtimeHeartbeatTests
{
    private const string Alpha = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Beta = "bbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task PlatformRevocationRetainsOwnUserDiscoveryAndTenantInterests()
    {
        var scenario = await Scenario.CreateAsync(admin: true);
        await scenario.Hub.Subscribe([
            new("platform"), new("user-discovery", ResourceId: "viewer"), new("fake", Alpha)
        ]);
        scenario.Tickets.Tickets["viewer"] = new AuthenticationTicket(IdentityFixture.User(),
            scenario.Ticket.Properties,
            RealtimeFeature.CookieScheme);
        var remaining = await scenario.Hub.Heartbeat();
        Assert.Equal(["user-discovery", "fake"], remaining.Select(value => value.Topic));
        Assert.Equal([null], scenario.Client.AccessRevocations);
        Assert.Equal(0, scenario.Client.SessionRevocations);
    }

    [Fact]
    public async Task InvalidUserAndTenantScopesCannotRevokePlatformOrTenantAccess()
    {
        var scenario = await Scenario.CreateAsync();
        await scenario.Hub.Subscribe([new("fake", Alpha)]);
        await Assert.ThrowsAsync<HubException>(() =>
            scenario.Hub.Subscribe([new("user-discovery", ResourceId: "other-user")]));
        await Assert.ThrowsAsync<HubException>(() => scenario.Hub.Subscribe([new("user-discovery")]));
        await Assert.ThrowsAsync<HubException>(() => scenario.Hub.Subscribe([new("fake")]));
        Assert.Empty(scenario.Client.AccessRevocations);
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Equal(Alpha, Assert.Single(await scenario.Hub.Heartbeat()).TenantId);
        Assert.True(Assert.Single(await scenario.Hub.Subscribe([new("user-discovery", ResourceId: "viewer")])).Covered);
    }

    [Fact]
    public async Task MixedAvailableDisabledAndUnknownTopicsKeepIndependentStableCoverage()
    {
        var scenario = await Scenario.CreateAsync(enabledTopics: ["fake"]);
        var initial = await scenario.Hub.Subscribe([
            new("fake", Alpha.ToUpperInvariant(), "Resource-A"), new("disabled", Alpha), new("unknown", Beta)
        ]);
        Assert.Equal([true, false, false], initial.Select(value => value.Covered));
        Assert.Equal(Alpha, initial[0].TenantId);
        Assert.Equal("Resource-A", initial[0].ResourceId);
        Assert.Equal("fake", Assert.Single(scenario.Subscriptions.Connections["connection"]).Request.Topic);
        Assert.Equal(initial, await scenario.Hub.Heartbeat());
        Assert.Equal(1, scenario.Owner.SubscribeCalls);
        Assert.Equal(1, scenario.Owner.RenewCalls);
        Assert.Equal(0, scenario.DisabledOwner.SubscribeCalls);
        Assert.Equal(0, scenario.UnknownOwner.SubscribeCalls);
        Assert.Equal(0, scenario.DisabledOwner.RenewCalls);
        Assert.Equal(0, scenario.UnknownOwner.RenewCalls);
        Assert.Empty(scenario.Client.AccessRevocations);
        Assert.Equal(0, scenario.Client.SessionRevocations);
        await scenario.Hub.Unsubscribe();
        Assert.Empty(await scenario.Hub.Heartbeat());
        Assert.Equal(1, scenario.Owner.UnsubscribeCalls);
        Assert.Equal(0, scenario.DisabledOwner.UnsubscribeCalls);
        Assert.Equal(0, scenario.UnknownOwner.UnsubscribeCalls);
    }

    [Theory]
    [InlineData("fake")]
    [InlineData("disabled")]
    public async Task InitialKnownTenantDenialNotifiesEvenWithoutExistingInterests(string topic)
    {
        var scenario = await Scenario.CreateAsync(enabledTopics: ["fake"]);
        scenario.Membership = (tenant, user) => new TenantMemberMissing(tenant, user);
        await Assert.ThrowsAsync<HubException>(() => scenario.Hub.Subscribe([new(topic, Alpha)]));
        Assert.Equal([Alpha], scenario.Client.AccessRevocations);
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Subscriptions.Connections);
        Assert.Equal(0, scenario.Owner.SubscribeCalls);
        Assert.Equal(0, scenario.DisabledOwner.SubscribeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlatformDenialIsScopedEvenWhenTheKnownTopicIsDisabled(bool disabled)
    {
        var scenario = await Scenario.CreateAsync(enabledTopics: disabled ? ["fake"] : []);
        await scenario.Hub.Subscribe([new("fake", Alpha)]);
        await Assert.ThrowsAsync<HubException>(() => scenario.Hub.Subscribe([new("platform")]));
        Assert.Equal([null], scenario.Client.AccessRevocations);
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Equal(Alpha, Assert.Single(scenario.Subscriptions.Connections["connection"]).Request.TenantId);
        Assert.True(Assert.Single(await scenario.Hub.Heartbeat()).Covered);
    }

    [Fact]
    public async Task DisabledTopicDenialDuringReplacementFencesAllOwnedRequestsInThatTenant()
    {
        var scenario = await Scenario.CreateAsync(enabledTopics: ["fake"]);
        await scenario.Hub.Subscribe([
            new("fake", Alpha), new("disabled", Alpha), new("unknown", Alpha), new("fake", Beta)
        ]);
        scenario.Membership = (tenant, user) => tenant == Alpha
            ? new TenantMemberMissing(tenant, user)
            : new TenantMemberSnapshot(tenant, user, TenantMembershipRole.Viewer);
        await Assert.ThrowsAsync<HubException>(() => scenario.Hub.Subscribe([new("disabled", Alpha)]));
        Assert.Equal(Beta, Assert.Single(await scenario.Hub.Heartbeat()).TenantId);
        Assert.Equal([Alpha], scenario.Client.AccessRevocations);
        Assert.Equal(1, scenario.Owner.UnsubscribeCalls);
        Assert.Equal(0, scenario.DisabledOwner.UnsubscribeCalls);
        Assert.Equal(0, scenario.UnknownOwner.UnsubscribeCalls);
        Assert.Equal(0, scenario.Client.SessionRevocations);
    }

    [Fact]
    public async Task UnknownTopicDoesNotAuthorizeOrRevokeItsSuppliedTenantDuringHeartbeat()
    {
        var scenario = await Scenario.CreateAsync(enabledTopics: ["fake"]);
        var checkedTenants = new List<string>();
        scenario.Membership = (tenant, user) =>
        {
            checkedTenants.Add(tenant);
            return tenant == Beta
                ? new TenantMemberMissing(tenant, user)
                : new TenantMemberSnapshot(tenant, user, TenantMembershipRole.Viewer);
        };
        var initial = await scenario.Hub.Subscribe([new("unknown", Beta), new("fake", Alpha)]);
        Assert.Equal(initial, await scenario.Hub.Heartbeat());
        Assert.DoesNotContain(Beta, checkedTenants);
        Assert.Empty(scenario.Client.AccessRevocations);
        Assert.Equal(0, scenario.UnknownOwner.SubscribeCalls);
        Assert.Equal(0, scenario.UnknownOwner.RenewCalls);
        Assert.Equal(0, scenario.Client.SessionRevocations);
    }

    [Fact]
    public async Task DisabledKnownScopeStillRechecksMembershipOnHeartbeatWithoutInvokingItsOwner()
    {
        var scenario = await Scenario.CreateAsync(enabledTopics: ["fake"]);
        await scenario.Hub.Subscribe([new("disabled", Alpha), new("unknown", Alpha), new("fake", Beta)]);
        scenario.Membership = (tenant, user) => tenant == Alpha
            ? new TenantMemberMissing(tenant, user)
            : new TenantMemberSnapshot(tenant, user, TenantMembershipRole.Viewer);
        Assert.Equal(Beta, Assert.Single(await scenario.Hub.Heartbeat()).TenantId);
        Assert.Equal([Alpha], scenario.Client.AccessRevocations);
        Assert.Equal(0, scenario.DisabledOwner.RenewCalls);
        Assert.Equal(0, scenario.DisabledOwner.UnsubscribeCalls);
        Assert.Equal(0, scenario.UnknownOwner.UnsubscribeCalls);
    }

    [Fact]
    public async Task UnavailableOnlyAcknowledgementsExpireAtTheirOriginalDeadlineAndHonorExplicitRevocation()
    {
        var scenario = await Scenario.CreateAsync(enabledTopics: ["fake"]);
        var initial = await scenario.Hub.Subscribe([new("disabled", Alpha), new("unknown", Beta)]);
        Assert.All(initial, value => Assert.False(value.Covered));
        Assert.Empty(scenario.Subscriptions.Connections["connection"]);
        scenario.Ticket.Properties.ExpiresUtc = scenario.Clock.Now.AddMinutes(3);
        Assert.Equal(initial, await scenario.Hub.Heartbeat());
        scenario.Clock.Now = scenario.Clock.Now.AddMinutes(2);
        Assert.Empty(await scenario.Hub.Heartbeat());
        Assert.Empty(scenario.Client.AccessRevocations);
        var newAcknowledgements = await scenario.Hub.Subscribe([new("unknown", Beta)]);
        Assert.NotEqual(initial[1].SubscriptionId, Assert.Single(newAcknowledgements).SubscriptionId);
        // No active Redis entries exist to check the tombstone inside ReplaceAsync.
        await scenario.Subscriptions.RevokeSessionAsync("viewer", default);
        Assert.Empty(await scenario.Hub.Heartbeat());
        Assert.Equal(1, scenario.Client.SessionRevocations);
        Assert.Equal(0, scenario.UnknownOwner.UnsubscribeCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnavailableOnlyTicketLossDistinguishesExplicitMarkersFromTransientStorageLoss(bool revoked,
        bool corruptNull)
    {
        var scenario = await Scenario.CreateAsync(enabledTopics: ["fake"]);
        var ticket = scenario.Ticket;
        var initial = await scenario.Hub.Subscribe([new("disabled", Alpha), new("unknown", Beta)]);
        if (corruptNull)
            scenario.Tickets.ReadAsUnavailable = true;
        else
            await scenario.Tickets.RemoveAsync("viewer");
        if (revoked)
        {
            await scenario.Subscriptions.RevokeSessionAsync("viewer", default);
            Assert.Empty(await scenario.Hub.Heartbeat());
        }
        else
        {
            await Assert.ThrowsAsync<HubException>(() => scenario.Hub.Heartbeat());
            scenario.Tickets.ReadAsUnavailable = false;
            await scenario.Tickets.StoreAsync(ticket);
            var recovered = await scenario.Hub.Subscribe([new("disabled", Alpha), new("unknown", Beta)]);
            Assert.All(recovered, value => Assert.False(value.Covered));
            Assert.DoesNotContain(recovered,
                value => initial.Any(previous => previous.SubscriptionId == value.SubscriptionId));
            Assert.Equal(recovered, await scenario.Hub.Heartbeat());
        }

        Assert.Equal(revoked ? 1 : 0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Client.AccessRevocations);
        Assert.Empty(scenario.Client.Changes);
        Assert.Equal(0, scenario.DisabledOwner.SubscribeCalls);
        Assert.Equal(0, scenario.DisabledOwner.RenewCalls);
        Assert.Equal(0, scenario.DisabledOwner.UnsubscribeCalls);
        Assert.Equal(0, scenario.UnknownOwner.SubscribeCalls);
        Assert.Equal(0, scenario.UnknownOwner.UnsubscribeCalls);
    }

    [Fact]
    public async Task FailedMixedReplacementLeavesNoAcknowledgementsOrOwnerInterestsToRenew()
    {
        var scenario = await Scenario.CreateAsync(enabledTopics: ["fake"]);
        await scenario.Hub.Subscribe([new("fake", Alpha), new("disabled", Alpha), new("unknown", Beta)]);
        scenario.Subscriptions.ReplaceFailure = new InvalidOperationException("storage unavailable");
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Hub.Subscribe([
            new("fake", Alpha), new("disabled", Alpha), new("unknown", Beta)
        ]));
        scenario.Subscriptions.ReplaceFailure = null;
        Assert.Empty(await scenario.Hub.Heartbeat());
        Assert.Empty(scenario.Owner.Interests);
        Assert.Equal(0, scenario.Owner.RenewCalls);
        Assert.Equal(0, scenario.DisabledOwner.UnsubscribeCalls);
        Assert.Equal(0, scenario.UnknownOwner.UnsubscribeCalls);
    }

    [Fact]
    public async Task RenewalPreservesSubscriptionIdsAndProviderInterests_AndReturnsOnlyAcknowledgements()
    {
        var scenario = await Scenario.CreateAsync();
        var initial = await scenario.Hub.Subscribe([new("fake", Alpha, "Resource-A")]);
        var original = Assert.Single(scenario.Subscriptions.Connections["connection"]);
        var removeCalls = scenario.Subscriptions.RemoveCalls;
        var result = await scenario.Hub.Heartbeat();
        Assert.Equal(initial, result);
        Assert.Equal(original, Assert.Single(scenario.Subscriptions.Connections["connection"]));
        Assert.Equal(1, scenario.Owner.SubscribeCalls);
        Assert.Equal(1, scenario.Owner.RenewCalls);
        Assert.Equal(0, scenario.Owner.UnsubscribeCalls);
        Assert.Equal(removeCalls, scenario.Subscriptions.RemoveCalls);
        Assert.Equal(0, scenario.Tickets.RenewCalls);
        Assert.Empty(scenario.Client.Changes);
        var json = JsonSerializer.SerializeToElement(Assert.Single(result),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(["covered", "resourceId", "subscriptionId", "tenantId", "topic"],
            json.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task ProviderRenewalMayDeclineCoverageWithoutReplacingItsInterest()
    {
        var scenario = await Scenario.CreateAsync();
        var initial = Assert.Single(await scenario.Hub.Subscribe([new("fake", Alpha)]));
        scenario.Owner.Covered = false;
        var renewed = Assert.Single(await scenario.Hub.Heartbeat());
        Assert.Equal(initial.SubscriptionId, renewed.SubscriptionId);
        Assert.False(renewed.Covered);
        Assert.Equal([initial.SubscriptionId], scenario.Owner.Interests);
        Assert.Equal(1, scenario.Owner.SubscribeCalls);
        Assert.Equal(0, scenario.Owner.UnsubscribeCalls);
        scenario.Owner.Covered = true;
        Assert.True(Assert.Single(await scenario.Hub.Heartbeat()).Covered);
        Assert.Equal(1, scenario.Owner.SubscribeCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnexpectedTicketOrRegistryFailureProducesNoAcknowledgementOrPermanentRevocation(
        bool ticketFailure)
    {
        var scenario = await Scenario.CreateAsync();
        var initial = Assert.Single(await scenario.Hub.Subscribe([new("fake", Alpha)]));
        if (ticketFailure)
            scenario.Tickets.RetrieveFailure = new InvalidOperationException("storage unavailable");
        else
            scenario.Subscriptions.ReplaceFailure = new InvalidOperationException("storage unavailable");
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Hub.Heartbeat());
        Assert.Equal([initial.SubscriptionId], scenario.Owner.Interests);
        Assert.Equal(0, scenario.Owner.UnsubscribeCalls);
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Client.AccessRevocations);
        Assert.Empty(scenario.Client.Changes);
    }

    [Theory]
    [InlineData("explicit-missing")]
    [InlineData("revoked-during-registry-update")]
    public async Task UnavailableSessionReleasesInterestsAndEmitsSessionRevoked(string reason)
    {
        var scenario = await Scenario.CreateAsync();
        await scenario.Hub.Subscribe([new("fake", Alpha)]);
        if (reason == "explicit-missing")
        {
            await scenario.Tickets.RemoveAsync("viewer");
            await scenario.Subscriptions.RevokeSessionAsync("viewer", default);
        }
        else
            scenario.Subscriptions.ReplaceFailure = new RealtimeSessionUnavailableException();

        Assert.Empty(await scenario.Hub.Heartbeat());
        Assert.Empty(scenario.Subscriptions.Connections);
        Assert.Empty(scenario.Owner.Interests);
        Assert.Equal(1, scenario.Owner.UnsubscribeCalls);
        Assert.Equal(1, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Client.Changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TicketLossOrCorruptNullBeforeExpiryPreservesWorkspaceIdentityAndRequestsBootstrap(
        bool corruptNull)
    {
        var scenario = await Scenario.CreateAsync();
        await scenario.Hub.Subscribe([new("fake", Alpha)]);
        var identity = scenario.Hub.Context.User;
        Assert.NotNull(identity);
        if (corruptNull)
            scenario.Tickets.ReadAsUnavailable = true;
        else
            await scenario.Tickets.RemoveAsync("viewer");
        scenario.Subscriptions.RevocationChecks.Clear();
        var error = await Assert.ThrowsAsync<HubException>(() => scenario.Hub.Heartbeat());
        Assert.Equal("The realtime session is unavailable.", error.Message);
        Assert.Same(identity, scenario.Hub.Context.User);
        Assert.True(identity.Identity!.IsAuthenticated);
        Assert.Empty(scenario.Subscriptions.Connections);
        Assert.Empty(scenario.Owner.Interests);
        Assert.Equal(1, scenario.Owner.UnsubscribeCalls);
        Assert.Equal(["viewer"], scenario.Subscriptions.RevocationChecks.Distinct());
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Client.AccessRevocations);
        Assert.Empty(scenario.Client.Changes);
        Assert.Equal(0, scenario.Tickets.RenewCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExplicitMarkerRevokesMissingOrExpiredSessionsIncludingEmptyOwnedLists(bool expiredTicket,
        bool emptyOwned)
    {
        var scenario = await Scenario.CreateAsync();
        if (!emptyOwned)
            await scenario.Hub.Subscribe([new("fake", Alpha)]);
        if (expiredTicket)
            scenario.Clock.Now = scenario.Clock.Now.AddMinutes(2);
        else
            await scenario.Tickets.RemoveAsync("viewer");
        await scenario.Subscriptions.RevokeSessionAsync("viewer", default);
        scenario.Subscriptions.RevocationChecks.Clear();
        Assert.Empty(await scenario.Hub.Heartbeat());
        Assert.Equal(["viewer"], scenario.Subscriptions.RevocationChecks.Distinct());
        Assert.Empty(scenario.Subscriptions.Connections);
        Assert.Empty(scenario.Owner.Interests);
        Assert.Equal(emptyOwned ? 0 : 1, scenario.Owner.UnsubscribeCalls);
        Assert.Equal(1, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Client.AccessRevocations);
        Assert.Empty(scenario.Client.Changes);
    }

    [Fact]
    public async Task RevocationMarkerStorageFailurePropagatesWithoutAcknowledgementOrLogout()
    {
        var scenario = await Scenario.CreateAsync();
        var initial = Assert.Single(await scenario.Hub.Subscribe([new("fake", Alpha)]));
        await scenario.Tickets.RemoveAsync("viewer");
        scenario.Subscriptions.RevocationCheckFailure = new InvalidOperationException("storage unavailable");
        scenario.Subscriptions.RevocationChecks.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Hub.Heartbeat());
        Assert.Equal(["viewer"], scenario.Subscriptions.RevocationChecks.Distinct());
        Assert.Equal([initial.SubscriptionId], scenario.Owner.Interests);
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Client.AccessRevocations);
        Assert.Empty(scenario.Client.Changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NaturalExpiryOrLateMissingTicketReleasesInterestsAndRequestsBootstrapWithoutRevocation(
        bool missingTicket)
    {
        var scenario = await Scenario.CreateAsync();
        await scenario.Hub.Subscribe([new("fake", Alpha)]);
        scenario.Clock.Now = scenario.Clock.Now.AddMinutes(2);
        if (missingTicket)
            await scenario.Tickets.RemoveAsync("viewer");
        var error = await Assert.ThrowsAsync<HubException>(() => scenario.Hub.Heartbeat());
        Assert.Equal("The realtime session is unavailable.", error.Message);
        Assert.Empty(scenario.Subscriptions.Connections);
        Assert.Empty(scenario.Owner.Interests);
        Assert.Equal(1, scenario.Owner.UnsubscribeCalls);
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Client.AccessRevocations);
        Assert.Empty(scenario.Client.Changes);
        Assert.Equal(0, scenario.Tickets.RenewCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NaturalExpiryDuringOwnerRenewalReleasesInterestsWithoutRevocation(bool missingTicket)
    {
        var scenario = await Scenario.CreateAsync();
        await scenario.Hub.Subscribe([new("fake", Alpha)]);
        scenario.Owner.OnRenew = () =>
        {
            scenario.Clock.Now = scenario.Clock.Now.AddMinutes(2);
            if (missingTicket)
                scenario.Tickets.Tickets.TryRemove("viewer", out _);
        };
        await Assert.ThrowsAsync<HubException>(() => scenario.Hub.Heartbeat());
        Assert.Empty(scenario.Subscriptions.Connections);
        Assert.Empty(scenario.Owner.Interests);
        Assert.Equal(1, scenario.Owner.UnsubscribeCalls);
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Client.AccessRevocations);
    }

    [Fact]
    public async Task KnownExplicitRevocationRemainsRevocationEvenAfterTheOriginalDeadline()
    {
        var scenario = await Scenario.CreateAsync();
        await scenario.Hub.Subscribe([new("fake", Alpha)]);
        scenario.Clock.Now = scenario.Clock.Now.AddMinutes(2);
        scenario.Tickets.RetrieveFailure = new RealtimeSessionUnavailableException();
        Assert.Empty(await scenario.Hub.Heartbeat());
        Assert.Empty(scenario.Subscriptions.Connections);
        Assert.Empty(scenario.Owner.Interests);
        Assert.Equal(1, scenario.Client.SessionRevocations);
    }

    [Fact]
    public async Task MembershipRemovalReleasesOnlyThatTenantsInterests()
    {
        var scenario = await Scenario.CreateAsync();
        await scenario.Hub.Subscribe([new("fake", Alpha), new("fake", Alpha, "resource"), new("fake", Beta)]);
        scenario.Membership = (tenant, user) => tenant == Alpha
            ? new TenantMemberMissing(tenant, user)
            : new TenantMemberSnapshot(tenant, user, TenantMembershipRole.Viewer);
        Assert.Equal(Beta, Assert.Single(await scenario.Hub.Heartbeat()).TenantId);
        Assert.Equal([Alpha], scenario.Client.AccessRevocations);
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Equal(2, scenario.Owner.UnsubscribeCalls);
        Assert.Equal(Beta, Assert.Single(scenario.Subscriptions.Connections["connection"]).Request.TenantId);
    }

    [Fact]
    public async Task DenialDuringChecksFencesEarlierAuthorizedRequestsInTheSameTenant()
    {
        var scenario = await Scenario.CreateAsync();
        await scenario.Hub.Subscribe([new("fake", Alpha), new("fake", Alpha, "resource")]);
        var checks = 0;
        scenario.Membership = (tenant, user) => ++checks == 1
            ? new TenantMemberSnapshot(tenant, user, TenantMembershipRole.Viewer)
            : new TenantMemberMissing(tenant, user);
        Assert.Empty(await scenario.Hub.Heartbeat());
        Assert.Empty(scenario.Owner.Interests);
        Assert.Empty(scenario.Subscriptions.Connections["connection"]);
        Assert.Equal([Alpha], scenario.Client.AccessRevocations);
        Assert.Equal(0, scenario.Owner.RenewCalls);
    }

    [Fact]
    public async Task TicketLossDuringProviderRenewalRequestsBootstrapWithoutRevocation()
    {
        var scenario = await Scenario.CreateAsync();
        await scenario.Hub.Subscribe([new("fake", Alpha)]);
        scenario.Owner.OnRenew = () => scenario.Tickets.Tickets.TryRemove("viewer", out _);
        await Assert.ThrowsAsync<HubException>(() => scenario.Hub.Heartbeat());
        Assert.Empty(scenario.Owner.Interests);
        Assert.Empty(scenario.Subscriptions.Connections);
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Client.AccessRevocations);
    }

    [Fact]
    public async Task MembershipRevokedDuringProviderRenewalFencesEveryInterestInItsTenant()
    {
        var scenario = await Scenario.CreateAsync();
        await scenario.Hub.Subscribe([new("fake", Alpha), new("fake", Alpha, "resource")]);
        scenario.Owner.OnRenew = () => scenario.Membership = (tenant, user) => new TenantMemberMissing(tenant, user);
        Assert.Empty(await scenario.Hub.Heartbeat());
        Assert.Empty(scenario.Owner.Interests);
        Assert.Equal([Alpha], scenario.Client.AccessRevocations);
        Assert.Equal(2, scenario.Owner.UnsubscribeCalls);
    }

    [Fact]
    public async Task RenewalNeverExtendsOriginalOrPreviouslyReducedExpiry()
    {
        var scenario = await Scenario.CreateAsync();
        await scenario.Hub.Subscribe([new("fake", Alpha)]);
        var original = Assert.Single(scenario.Subscriptions.Connections["connection"]).ExpiresAtUtc;
        scenario.Ticket.Properties.ExpiresUtc = original.AddMinutes(2);
        await scenario.Hub.Heartbeat();
        Assert.Equal(original, Assert.Single(scenario.Subscriptions.Connections["connection"]).ExpiresAtUtc);
        var shortened = scenario.Clock.Now.AddSeconds(30);
        scenario.Ticket.Properties.ExpiresUtc = shortened;
        await scenario.Hub.Heartbeat();
        Assert.Equal(shortened, Assert.Single(scenario.Subscriptions.Connections["connection"]).ExpiresAtUtc);
        scenario.Ticket.Properties.ExpiresUtc = original.AddMinutes(3);
        await scenario.Hub.Heartbeat();
        Assert.Equal(shortened, Assert.Single(scenario.Subscriptions.Connections["connection"]).ExpiresAtUtc);
        Assert.Equal(0, scenario.Tickets.RenewCalls);
    }

    [Fact]
    public async Task ExpiredOriginalInterestIsReleasedEvenIfItsTicketExpiryMovesLater()
    {
        var scenario = await Scenario.CreateAsync();
        await scenario.Hub.Subscribe([new("fake", Alpha)]);
        scenario.Ticket.Properties.ExpiresUtc = scenario.Clock.Now.AddMinutes(4);
        scenario.Clock.Now = scenario.Clock.Now.AddMinutes(2);
        Assert.Empty(await scenario.Hub.Heartbeat());
        Assert.Empty(scenario.Owner.Interests);
        Assert.Equal(1, scenario.Owner.UnsubscribeCalls);
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Client.AccessRevocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshTicketRolesAreUsedInsteadOfTheOriginalConnectionIdentity(bool disabled)
    {
        var scenario = await Scenario.CreateAsync(admin: true, enabledTopics: disabled ? ["fake"] : []);
        await scenario.Hub.Subscribe([new("platform")]);
        scenario.Tickets.Tickets["viewer"] = new AuthenticationTicket(IdentityFixture.User(),
            scenario.Ticket.Properties,
            RealtimeFeature.CookieScheme);
        Assert.Empty(await scenario.Hub.Heartbeat());
        Assert.Equal([null], scenario.Client.AccessRevocations);
        Assert.Equal(0, scenario.Client.SessionRevocations);
    }

    [Fact]
    public async Task EmptyOwnedListStillChecksLiveStorageWithoutCreatingCoverage()
    {
        var scenario = await Scenario.CreateAsync();
        Assert.Empty(await scenario.Hub.Heartbeat());
        Assert.Equal(1, scenario.Subscriptions.ReplaceCalls);
        Assert.Equal(0, scenario.Owner.SubscribeCalls);
        scenario.Tickets.RetrieveFailure = new InvalidOperationException("storage unavailable");
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Hub.Heartbeat());
        Assert.Equal(0, scenario.Client.SessionRevocations);
    }

    [Fact]
    public async Task UnknownMissingTicketWithoutOwnedInterestsRequestsBootstrapWithoutRevocation()
    {
        var scenario = await Scenario.CreateAsync();
        Assert.Empty(await scenario.Hub.Heartbeat());
        await scenario.Tickets.RemoveAsync("viewer");
        var error = await Assert.ThrowsAsync<HubException>(() => scenario.Hub.Heartbeat());
        Assert.Equal("The realtime session is unavailable.", error.Message);
        Assert.Empty(scenario.Subscriptions.Connections);
        Assert.Empty(scenario.Owner.Interests);
        Assert.Equal(0, scenario.Owner.UnsubscribeCalls);
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Client.AccessRevocations);
        Assert.Empty(scenario.Client.Changes);
    }

    [Fact]
    public async Task SubscribeCanonicalizesTenantObjectIdsAndPreservesResourceIdentity()
    {
        var scenario = await Scenario.CreateAsync();
        var checkedTenants = new List<string>();
        scenario.Membership = (tenant, user) =>
        {
            checkedTenants.Add(tenant);
            return new TenantMemberSnapshot(tenant, user, TenantMembershipRole.Viewer);
        };
        var acknowledgement =
            Assert.Single(await scenario.Hub.Subscribe([new("fake", Alpha.ToUpperInvariant(), "Resource-A")]));
        Assert.Equal(Alpha, acknowledgement.TenantId);
        Assert.Equal("Resource-A", acknowledgement.ResourceId);
        Assert.Equal([Alpha], checkedTenants.Distinct());
        Assert.Single(await scenario.Subscriptions.FindAsync("fake", Alpha, default));
        await Assert.ThrowsAsync<HubException>(() =>
            scenario.Hub.Subscribe([new("fake", Alpha), new("fake", Alpha.ToUpperInvariant())]));
        var distinctResources =
            await scenario.Hub.Subscribe([new("fake", Alpha, "Resource-A"), new("fake", Alpha, "resource-a")]);
        Assert.Equal(2, distinctResources.Count);
    }

    private sealed class Scenario
    {
        public MemoryTickets Tickets { get; } = new();

        public MemorySubscriptions Subscriptions { get; } = new();

        public RenewalLifecycle Owner { get; } = new();

        public RenewalLifecycle DisabledOwner { get; } = new("disabled");

        public RenewalLifecycle UnknownOwner { get; } = new("unknown");

        public RecordingClient Client { get; } = new();

        public FixedClock Clock { get; } = new();

        public AuthenticationTicket Ticket => Tickets.Tickets["viewer"];

        public RealtimeHub Hub { get; private set; } = null!;

        public Func<string, string, LoadTenantMemberOutcome> Membership { get; set; } =
            (tenant, user) => new TenantMemberSnapshot(tenant, user, TenantMembershipRole.Viewer);

        public static async Task<Scenario> CreateAsync(bool admin = false, string[]? enabledTopics = null)
        {
            var scenario = new Scenario();
            var principal = IdentityFixture.User(admin: admin);
            await scenario.Tickets.StoreAsync(new AuthenticationTicket(principal,
                new AuthenticationProperties { ExpiresUtc = scenario.Clock.Now.AddMinutes(1) },
                RealtimeFeature.CookieScheme));
            var registry = new RealtimeTopicRegistry([
                    new("fake", RealtimeScope.Tenant), new("disabled", RealtimeScope.Tenant),
                    new("platform", RealtimeScope.Platform),
                    new("user-discovery", RealtimeScope.User)
                ],
                new()
                {
                    Enabled = true,
                    EnabledTopics = enabledTopics ?? []
                });
            var access = new RealtimeAccess((request, _, _) => Task.FromResult(scenario.Membership(request.TenantId,
                request.UserId)),
                (tenant, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentitySnapshot(tenant, "name")),
                registry);
            scenario.Hub = new RealtimeHub(scenario.Subscriptions,
                    scenario.Tickets,
                    access,
                    [scenario.Owner, scenario.DisabledOwner, scenario.UnknownOwner],
                    scenario.Clock)
            {
                Context = new CallerContext(principal),
                Clients = new RecordingClients(scenario.Client)
            };
            return scenario;
        }
    }

    private sealed class RenewalLifecycle(string topic = "fake") : IRealtimeTopicLifecycle
    {
        public string Topic => topic;

        public HashSet<string> Interests { get; } = [];

        public int SubscribeCalls { get; private set; }

        public int RenewCalls { get; private set; }

        public int UnsubscribeCalls { get; private set; }

        public bool Covered { get; set; } = true;

        public Action? OnRenew { get; set; }

        public Task<bool> SubscribeAsync(RealtimeSubscription subscription,
            ClaimsPrincipal principal,
            CancellationToken ct)
        {
            SubscribeCalls++;
            Interests.Add(subscription.Id);
            return Task.FromResult(true);
        }

        public Task<bool> RenewAsync(RealtimeSubscription subscription, ClaimsPrincipal principal, CancellationToken ct)
        {
            RenewCalls++;
            OnRenew?.Invoke();
            return Task.FromResult(Covered && Interests.Contains(subscription.Id));
        }

        public Task UnsubscribeAsync(RealtimeSubscription subscription, CancellationToken ct)
        {
            UnsubscribeCalls++;
            Interests.Remove(subscription.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CallerContext(ClaimsPrincipal principal) : HubCallerContext
    {
        public override string ConnectionId => "connection";
        public override string UserIdentifier => "viewer";
        public override ClaimsPrincipal User => principal;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }

    private sealed class RecordingClient : IRealtimeClient
    {
        public List<RealtimeChange> Changes { get; } = [];

        public List<string?> AccessRevocations { get; } = [];

        public int SessionRevocations { get; private set; }

        public Task Changed(RealtimeChange change)
        {
            Changes.Add(change);
            return Task.CompletedTask;
        }

        public Task SessionRevoked()
        {
            SessionRevocations++;
            return Task.CompletedTask;
        }

        public Task AccessRevoked(string? tenantId)
        {
            AccessRevocations.Add(tenantId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingClients(RecordingClient client) : IHubCallerClients<IRealtimeClient>
    {
        public IRealtimeClient Caller => client;
        public IRealtimeClient Others => client;
        public IRealtimeClient OthersInGroup(string groupName) => client;
        public IRealtimeClient All => client;
        public IRealtimeClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => client;
        public IRealtimeClient Client(string connectionId) => client;
        public IRealtimeClient Clients(IReadOnlyList<string> connectionIds) => client;
        public IRealtimeClient Group(string groupName) => client;
        public IRealtimeClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => client;
        public IRealtimeClient Groups(IReadOnlyList<string> groupNames) => client;
        public IRealtimeClient User(string userId) => client;
        public IRealtimeClient Users(IReadOnlyList<string> userIds) => client;
    }
}
