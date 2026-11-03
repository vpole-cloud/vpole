using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Endpoints;
using Xunit;

namespace Pole.Tests.Integration.Api;

public sealed class HttpStringEnumRequestTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("999")]
    [InlineData("invalid")]
    public void Deployment_connection_kind_requires_a_declared_name(string value)
    {
        var validation = new DeploymentTargetConnectionRequestBodyValidator()
            .Validate(new DeploymentTargetConnectionRequestBody(value));
        Assert.Contains(validation.Errors, error => error.PropertyName == "Kind");
    }

    [Fact]
    public void Deployment_connection_kind_retains_case_insensitive_names()
    {
        Assert.True(new DeploymentTargetConnectionRequestBodyValidator()
            .Validate(new DeploymentTargetConnectionRequestBody("incluster")).IsValid);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("999")]
    [InlineData("invalid")]
    public void Storage_profile_string_enums_require_declared_names(string value)
    {
        var valid = new CreateStorageProfileRequestBody(
            "fast", "Fast", "", "SSD", "Fast", true, false,
            [new StorageProfileTargetMappingRequestBody("507f1f77bcf86cd799439071", "fast")],
            ["pole/postgresql"], new StorageProfileTenantAccessDto("AllTenants", []), null);
        var validator = new CreateStorageProfileRequestBodyValidator();
        Assert.True(validator.Validate(valid).IsValid);
        Assert.Contains(validator.Validate(valid with { MediaType = value }).Errors,
            error => error.PropertyName == "MediaType");
        Assert.Contains(validator.Validate(valid with { PerformanceTier = value }).Errors,
            error => error.PropertyName == "PerformanceTier");
        Assert.Contains(validator.Validate(valid with { TenantAccess = new(value, []) }).Errors,
            error => error.PropertyName == "TenantAccess.Mode");
    }
}
