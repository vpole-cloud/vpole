using Pole.Features.Users.Application.Identity;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Sdk;

namespace Pole.Tests.Integration.Users;

[Collection(KeycloakIntegrationTestCollection.Name)]
public sealed class KeycloakIdentityProviderTests(
    KeycloakContainerFixture fixture
)
{
    [Fact]
    public async Task CreateUserAsync_CreatesUserInKeycloakAndReturnsId()
    {
        var email = CreateEmail();

        try
        {
            var result = await fixture.IdentityProvider.CreateUserAsync(
                new CreateIdentityUser(email, "Ada", "Lovelace"),
                CancellationToken.None);

            var userId = AssertCreated(result);
            Assert.False(string.IsNullOrWhiteSpace(userId));

            var user = await fixture.FindUserByUsernameAsync(email);

            Assert.NotNull(user);
            Assert.Equal(userId, user.Id);
            Assert.Equal(email, user.Username);
            Assert.Equal(email, user.Email);
            Assert.Equal("Ada", user.FirstName);
            Assert.Equal("Lovelace", user.LastName);
            Assert.True(user.Enabled);
            Assert.Contains("UPDATE_PASSWORD", user.RequiredActions ?? []);
            Assert.Contains("VERIFY_EMAIL", user.RequiredActions ?? []);
        }
        finally
        {
            await fixture.DeleteUserIfExistsAsync(email);
        }
    }

    [Fact]
    public async Task CreateUserAsync_WithDuplicateEmail_ReturnsFailure()
    {
        var email = CreateEmail();

        try
        {
            var first = await fixture.IdentityProvider.CreateUserAsync(
                new CreateIdentityUser(email, "Grace", "Hopper"),
                CancellationToken.None);
            AssertCreated(first);

            var duplicate = await fixture.IdentityProvider.CreateUserAsync(
                new CreateIdentityUser(email, "Grace", "Hopper"),
                CancellationToken.None);

            Assert.True(duplicate is IdentityUserCreationFailed);
        }
        finally
        {
            await fixture.DeleteUserIfExistsAsync(email);
        }
    }

    [Fact]
    public async Task DeleteUserAsync_DeletesExistingUser()
    {
        var email = CreateEmail();

        var create = await fixture.IdentityProvider.CreateUserAsync(
            new CreateIdentityUser(email, "Katherine", "Johnson"),
            CancellationToken.None);
        var userId = AssertCreated(create);

        var deleted = await fixture.IdentityProvider.DeleteUserAsync(userId, CancellationToken.None);
        var user = await fixture.FindUserByUsernameAsync(email);

        Assert.True(deleted);
        Assert.Null(user);
    }

    [Fact]
    public async Task DeleteUserAsync_WhenUserDoesNotExist_ReturnsTrue()
    {
        var deleted = await fixture.IdentityProvider.DeleteUserAsync(
            Guid.NewGuid().ToString("N"),
            CancellationToken.None);

        Assert.True(deleted);
    }

    [Fact]
    public async Task CreateUserAsync_WhenAdminSecretIsInvalid_ReturnsFailure()
    {
        var email = CreateEmail();
        var provider = fixture.CreateIdentityProvider("invalid-admin-secret");

        var result = await provider.CreateUserAsync(
            new CreateIdentityUser(email, "Dorothy", "Vaughan"),
            CancellationToken.None);
        var user = await fixture.FindUserByUsernameAsync(email);

        Assert.True(result is IdentityUserCreationFailed);
        Assert.Null(user);
    }

    private static string AssertCreated(CreateIdentityUserOutcome result) => result switch
    {
        IdentityUserCreated success => success.UserId,
        IdentityUserCreationFailed => throw new XunitException("Expected Keycloak user creation to succeed."),
    };

    private static string CreateEmail() => $"pole-it-{Guid.NewGuid():N}@example.test";
}
