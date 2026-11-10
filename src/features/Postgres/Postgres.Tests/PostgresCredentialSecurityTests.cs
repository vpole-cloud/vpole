using System.Collections.Immutable;
using Microsoft.FSharp.Core;
using MongoDB.Bson;
using Pole.Features.Postgres.Application;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Credentials;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Contracts;
using Xunit;
using Xunit.Sdk;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresCredentialSecurityTests
{
    private const string ClusterId = "507f1f77bcf86cd799439022";

    [Fact]
    public async Task Create_DelegatesTenantOwnerAndBasicAuthenticationValuesToSecrets()
    {
        var creator = new PostgresCredentialCreator((request, ct) =>
        {
            Assert.Equal(TestData.TenantId, request.TenantId);
            Assert.True(ObjectId.TryParse(request.SecretId, out _));
            Assert.Equal(new ManagedSecretOwner("pole/postgresql", ClusterId), request.Owner);
            Assert.Equal($"postgres-{ClusterId}-credentials", request.Name);
            Assert.Equal(1, request.Version);
            Assert.Equal(ManagedSecretKubernetesType.BasicAuthentication, request.KubernetesType);
            Assert.Equal("database-user", request.Values["username"]);
            Assert.Equal("private-password", request.Values["password"]);
            return Task.FromResult<PrepareManagedSecretOutcome>(
                Prepared(
                    request.TenantId,
                    request.SecretId,
                    request.Owner,
                    request.Name,
                    request.Version,
                    request.KubernetesType));
        });

        var result = await creator.InvokeAsync(
            TestData.TenantId,
            ClusterId,
            "database-user",
            "private-password",
            default);

        Assert.True(result.IsPostgresSuccess);
    }

    [Fact]
    public async Task Rotation_PreservesSecretIdentityOwnerTypeAndUsernameAndAdvancesVersion()
    {
        var current = Values();
        var rotator = new PostgresCredentialRotator((request, ct) =>
        {
            Assert.Equal(TestData.TenantId, request.TenantId);
            Assert.Equal(current.SecretId, request.SecretId);
            Assert.Equal(current.Name, request.Name);
            Assert.Equal(current.Version + 1, request.Version);
            Assert.Equal(current.KubernetesType, request.KubernetesType);
            Assert.Equal(new ManagedSecretOwner("pole/postgresql", ClusterId), request.Owner);
            Assert.Equal(current.Values["username"], request.Values["username"]);
            Assert.Equal("rotated-password", request.Values["password"]);
            return Task.FromResult<PrepareManagedSecretOutcome>(
                Prepared(
                    request.TenantId,
                    request.SecretId,
                    request.Owner,
                    request.Name,
                    request.Version,
                    request.KubernetesType));
        });

        var result = await rotator.InvokeAsync(TestData.TenantId, ClusterId, current, "rotated-password", default);

        Assert.True(result.IsPostgresSuccess);
        Assert.Equal("private-password", current.Values["password"]);
    }

    [Fact]
    public async Task ProviderFailureBecomesSafeCredentialProtectionFailure()
    {
        var creator = new PostgresCredentialCreator((request, _) =>
            Task.FromResult<PrepareManagedSecretOutcome>(new ManagedSecretEncryptionUnavailable()));
        var result = await creator.InvokeAsync(
            TestData.TenantId,
            ClusterId,
            "database-user",
            "private-password",
            default);
        Assert.True(result.IsPostgresError);
        Assert.DoesNotContain("private-password", result.ToString());
    }

    [Fact]
    public async Task CredentialReadsAddressTheManagedOwnerWithinTheRequestedTenant()
    {
        var loader = new ManagedLoadPostgresCredential((
            tenant, owner,
            ct) =>
        {
            Assert.Equal(TestData.TenantId, tenant);
            Assert.Equal(new ManagedSecretOwner("pole/postgresql", ClusterId), owner);
            return Task.FromResult<ReadManagedSecretOutcome>(Values());
        });
        var resolver = new ManagedResolvePostgresClusterCredentials(new LoadPostgresCredential(loader.InvokeAsync));
        var result = await resolver.InvokeAsync(TestData.TenantId, ClusterId, default);
        var credentials = Assert
            .IsType<PostgresOutcome<PostgresClusterCredentials, PostgresCredentialError>.PostgresSuccess>(result)
            .Item;
        Assert.Equal("database-user", credentials.Username);
        Assert.Equal("private-password", credentials.Password);
        Assert.DoesNotContain("private-password", credentials.ToString());
        Assert.DoesNotContain("private-password", Values().ToString());
        Assert.DoesNotContain(
            "private-password",
            new UpdatePostgresClusterPasswordRequestBody("private-password").ToString());
    }

    [Fact]
    public void CreateBodyAndPasswordBodyNeverRenderThePassword()
    {
        var body = new CreatePostgresClusterRequestBody(
            "db",
            ClusterId,
            17,
            FSharpOption<int>.None,
            FSharpOption<int>.None,
            TestData.DefaultStorageProfileId,
            FSharpOption<string>.None,
            "database-user",
            "private-password",
            FSharpOption<PlacementRequest>.None);
        Assert.DoesNotContain("private-password", body.ToString());
        Assert.DoesNotContain(
            "private-password",
            new UpdatePostgresClusterPasswordRequestBody("private-password").ToString());
    }

    [Theory]
    [InlineData(32767, true)]
    [InlineData(32768, false)]
    public void CreateValidationMeasuresCombinedUtf8Bytes(int characters, bool valid)
    {
        var body = new CreatePostgresClusterRequestBody(
            "db",
            ClusterId,
            17,
            FSharpOption<int>.None,
            FSharpOption<int>.None,
            TestData.DefaultStorageProfileId,
            FSharpOption<string>.None,
            "u",
            new string('é', characters),
            FSharpOption<PlacementRequest>.None);
        var result = new CreatePostgresClusterValidator(new PostgresSettings()).Validate(
            new CreatePostgresClusterRequest(body, TestData.TenantId));
        Assert.Equal(valid, result.IsValid);
    }

    [Fact]
    public async Task RotationRejectsCombinedByteLimitBeforeCallingEncryptionProvider()
    {
        var rotator = new PostgresCredentialRotator((request, _) =>
            throw new XunitException("Oversized credentials must not reach encryption."));
        var password = new string('é', 32768);
        var request = new UpdatePostgresClusterPasswordRequest(
            ClusterId,
            new UpdatePostgresClusterPasswordRequestBody(password),
            TestData.TenantId);
        Assert.True(new UpdatePostgresClusterPasswordRequestValidator().Validate(request).IsValid);
        var result = await rotator.InvokeAsync(TestData.TenantId, ClusterId, Values(), password, default);
        var rejected = Assert
            .IsType<PostgresOutcome<PreparedManagedSecret, PostgresCredentialError>.PostgresError>(result)
            .Item;
        Assert.IsType<PostgresCredentialError.PostgresCredentialValuesTooLarge>(rejected);
    }

    private static ManagedSecretValues Values() => new(
        "secret-id",
        "managed-name",
        ManagedSecretKubernetesType.BasicAuthentication,
        4,
        ImmutableDictionary<string, string>.Empty.Add("username", "database-user").Add("password", "private-password"));

    private static PreparedManagedSecret Prepared(
        string tenant,
        string id,
        ManagedSecretOwner owner,
        string name,
        long version,
        ManagedSecretKubernetesType type) => new(id, tenant, owner, name, type, version, ["username", "password"], []);
}
