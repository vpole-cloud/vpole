using Pole.PrivateAccess.Prototype;
using Xunit;

namespace PrivateAccess.Tests;

public sealed class FixtureAuthorityTests
{
    [Fact]
    public async Task Incomplete_fixture_is_authority_unavailable_instead_of_an_unhandled_exception()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{}");
            var authority = new FileAuthority(path);
            var outcome = await authority.AuthorizeAsync(new AccessIdentity("tenant", "subject"),
                new AccessRequest("grant", "deployment", "endpoint"), CancellationToken.None);
            Assert.True(outcome is AuthorityUnavailable);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
