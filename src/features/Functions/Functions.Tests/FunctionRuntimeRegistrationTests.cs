using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Functions.Runtime;
using Pole.Features.Functions.Runtime.Activation;
using Xunit;

namespace Pole.Features.Functions.Tests;

public sealed class FunctionRuntimeRegistrationTests
{
    [Fact]
    public void AddFunctionRuntimeFeatureWithoutAgentAuthenticationDoesNotRegisterActivator()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddFunctionRuntimeFeature(configuration);

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IFunctionRevisionActivator));
    }
}
