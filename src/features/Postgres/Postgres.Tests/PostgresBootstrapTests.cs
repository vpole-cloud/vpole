using System.Reflection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Postgres.Application.Credentials;
using Pole.Features.Postgres.Domain;
using Pole.Features.Secrets.Contracts;
using Xunit;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresBootstrapTests
{
    [Fact]
    public async Task BootstrapUsesPinnedMetadataAndReturnsConfiguredOnlyAfterEncryptedCredentialCreation()
    {
        var request = Request();
        BootstrapSecretReady? accepted = null;
        var initializer = new PostgresBootstrapInitializer(
            (
                input, session,
                ct) =>
            {
                Assert.Equal(request.Cluster.TenantId, input.TenantId);
                Assert.Equal(request.Cluster.Id, input.Owner.ResourceId);
                Assert.Equal("pole/postgresql", input.Owner.ResourceType);
                Assert.Equal(request.Placement, input.Placement);
                Assert.Equal("postgres", input.Username);
                var bootstrap = Assert.IsType<PreparedPostgresProvisioning>(request.Cluster.Provisioning).Bootstrap;
                Assert.Equal(bootstrap.SourceSecretId, input.Source.SecretId);
                Assert.Equal(bootstrap.SourceKey, input.Source.Key);
                Assert.Equal(bootstrap.CredentialId, input.CredentialId);
                accepted = new BootstrapSecretReady(input, new SecretPlanningVersion(input.Source.SecretId, 4, 7), 12);
                return Task.FromResult<ReadBootstrapSecretOutcome>(accepted);
            },
            (
                ready, session,
                ct) =>
            {
                Assert.Same(accepted, ready);
                Assert.True(session.IsInTransaction);
                return Task.FromResult<CreateBootstrapSecretOutcome>(
                    new BootstrapSecretInitialized(ready.Request.CredentialId));
            });

        var outcome = await initializer.InvokeAsync(request, Session(), default);
        var initialized = Assert.IsType<PostgresBootstrapOutcome.PostgresBootstrapInitialized>(outcome);
        Assert.IsType<ConfiguredPostgresProvisioning>(initialized.Item.Provisioning);
        Assert.Equal(request.Cluster.Revision + 1, initialized.Item.Revision);
    }

    [Fact]
    public async Task ExistingCredentialNeverCopiesSourceAgainAfterRotation()
    {
        var request = Request();
        request = new PostgresBootstrapRequest(
            request.Cluster.ConfigurePrepared(new PostgresDesiredCapacity(1, 20, PostgresReplicationMode.Asynchronous)),
            request.Placement,
            request.Capacity);
        var initializer = new PostgresBootstrapInitializer(
            (
                input, _,
                _) => Task.FromResult<ReadBootstrapSecretOutcome>(new BootstrapSecretInitialized(input.CredentialId)),
            (
                _, _,
                _) => throw new InvalidOperationException("A configured credential must never be bootstrapped again."));

        var outcome = await initializer.InvokeAsync(request, Session(), default);
        Assert.Same(
            request.Cluster,
            Assert.IsType<PostgresBootstrapOutcome.PostgresBootstrapInitialized>(outcome).Item);
    }

    [Fact]
    public async Task MissingConfiguredCredentialDoesNotReinitializeFromSource()
    {
        var request = Request();
        request = new PostgresBootstrapRequest(
            request.Cluster.ConfigurePrepared(new PostgresDesiredCapacity(1, 20, PostgresReplicationMode.Asynchronous)),
            request.Placement,
            request.Capacity);
        var initializer = new PostgresBootstrapInitializer(
            (
                input, _,
                _) => Task.FromResult<ReadBootstrapSecretOutcome>(
                new BootstrapSecretReady(input, new SecretPlanningVersion(input.Source.SecretId, 1, 0), 1)),
            (
                _, _,
                _) => throw new InvalidOperationException("Reinitialization would overwrite password rotation."));

        var outcome = await initializer.InvokeAsync(request, Session(), default);
        Assert.True(outcome.IsPostgresBootstrapUnavailable);
    }

    [Fact]
    public async Task UnavailableValueKeepsIdentityPrepared()
    {
        var request = Request();
        var initializer = new PostgresBootstrapInitializer(
            (
                _, _,
                _) => Task.FromResult<ReadBootstrapSecretOutcome>(
                new BootstrapSecretUnavailable(ConsumerSecretReferenceError.SecretValuesMissing)),
            (
                _, _,
                _) => throw new InvalidOperationException("No value is available."));

        var outcome = await initializer.InvokeAsync(request, Session(), default);
        Assert.Equal(
            ConsumerSecretReferenceError.SecretValuesMissing,
            Assert.IsType<PostgresBootstrapOutcome.PostgresBootstrapUnavailable>(outcome).Item);
        Assert.True(request.Cluster.IsPrepared);
        Assert.Equal(1, request.Cluster.Revision);
    }

    private static PostgresBootstrapRequest Request()
    {
        var cluster = PostgresProvisioningTests.Prepared();
        return new PostgresBootstrapRequest(
            cluster,
            new BootstrapSecretPlacement(
                cluster.GroupId,
                ObjectId.GenerateNewId().ToString(),
                ObjectId.GenerateNewId().ToString()),
            new PostgresDesiredCapacity(1, 20, PostgresReplicationMode.Asynchronous));
    }

    private static IClientSessionHandle Session() => DispatchProxy.Create<IClientSessionHandle, TransactionSession>();

    public class TransactionSession : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_IsInTransaction" => true,
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }
}
