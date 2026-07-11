using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Harbor;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.ContainerRegistries;

public sealed class HarborContractTests
{
    [HarborContractFact]
    [Trait("Category", "HarborContract")]
    public async Task ProvisionProject_CreatesUsableProjectAndRobotCredentials()
    {
        var options = GetOptions();
        var client = new HarborContainerRegistryClient(new HttpClient(), options);
        var projectName = $"pole-contract-{Guid.NewGuid():N}"[..30];
        ProvisionedRegistryProject? provisioned = null;

        try
        {
            var result = await client.ProvisionProjectAsync(
                new RegistryProjectProvisioningRequest("contract-test", projectName));

            if (!result.TryGetValue(out var created, out var failure))
                Assert.Fail(failure.ToString());
            provisioned = created;
            Assert.Equal(projectName, created.ProjectName);
            Assert.False(string.IsNullOrWhiteSpace(created.BuildCredential.Username));
            Assert.False(string.IsNullOrWhiteSpace(created.BuildCredential.Secret));
            Assert.False(string.IsNullOrWhiteSpace(created.PullCredential.Username));
            Assert.False(string.IsNullOrWhiteSpace(created.PullCredential.Secret));
            Assert.NotEqual(created.BuildCredential.Username, created.PullCredential.Username);
        }
        finally
        {
            var cleanup = await client.DeleteProjectAsync(
                provisioned?.ExternalProjectId ?? projectName);
            Assert.IsAssignableFrom<ISuccess>(cleanup);
        }
    }

    private static HarborOptions GetOptions() =>
        new()
        {
            Enabled = true,
            BaseUrl = Environment.GetEnvironmentVariable("POLE_HARBOR_CONTRACT_BASE_URL")!,
            RegistryHost = Environment.GetEnvironmentVariable("POLE_HARBOR_CONTRACT_REGISTRY_HOST")!,
            Username = Environment.GetEnvironmentVariable("POLE_HARBOR_CONTRACT_USERNAME")!,
            Password = Environment.GetEnvironmentVariable("POLE_HARBOR_CONTRACT_PASSWORD")!,
            RobotDurationDays = -1
        };
}

public sealed class HarborContractFactAttribute : FactAttribute
{
    private static readonly string[] RequiredVariables =
    [
        "POLE_HARBOR_CONTRACT_BASE_URL",
        "POLE_HARBOR_CONTRACT_REGISTRY_HOST",
        "POLE_HARBOR_CONTRACT_USERNAME",
        "POLE_HARBOR_CONTRACT_PASSWORD"
    ];

    public HarborContractFactAttribute()
    {
        if (RequiredVariables.Any(name =>
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
        {
            Skip = "Set the POLE_HARBOR_CONTRACT_* variables to run against a real Harbor instance.";
        }
    }
}
