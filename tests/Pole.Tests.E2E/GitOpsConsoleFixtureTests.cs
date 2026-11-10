using System.Collections.Immutable;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class GitOpsConsoleFixtureTests
{
    [Theory]
    [InlineData(GitOpsConsoleFixtureNames.AdoptedGroup)]
    [InlineData(GitOpsConsoleFixtureNames.RenamedGroup)]
    public void LifecycleGroupIdentitiesSatisfyTheOwnerCreateContract(string name)
    {
        var result = new CreateResourceGroupValidator().Validate(
            new CreateResourceGroupRequest(
                new CreateResourceGroupRequestBody(name, "Manual console adoption", ImmutableHashSet<string>.Empty),
                "507f1f77bcf86cd799439011"));

        Assert.True(
            result.IsValid,
            string.Join("; ", result.Errors.Select(error => error.PropertyName + ": " + error.ErrorMessage)));
    }
}
