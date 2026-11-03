using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

public sealed class ComputeProfileRequestValidatorTests
{
    [Fact]
    public void Create_rejects_null_description_at_the_request_boundary()
    {
        var body = ValidBody() with { Description = null! };

        var result = new CreateComputeProfileRequestValidator().Validate(
            new CreateComputeProfileRequest(body));

        Assert.Contains(
            result.Errors,
            error => error.PropertyName == "Body.Description");
    }

    [Fact]
    public void Validator_reports_resource_limits_and_selected_tenant_shape()
    {
        var body = ValidBody() with
        {
            Resources = new ComputeResourceRequirementsRequestBody(
                new ComputeResourceValuesRequestBody(1000, 1024),
                new ComputeResourceValuesRequestBody(250, 256)),
            TenantAccess = new ComputeProfileTenantAccessRequestBody(
                "selected-tenants",
                [])
        };

        var validation = new CreateComputeProfileRequestValidator()
            .Validate(new CreateComputeProfileRequest(body));

        Assert.Contains(validation.Errors, error =>
            error.PropertyName.EndsWith("Limits.CpuMillis", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error =>
            error.PropertyName.EndsWith("Limits.MemoryMi", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error =>
            error.PropertyName.EndsWith("TenantIds", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_reports_duplicate_target_mappings_before_contextual_io()
    {
        var mapping = ValidBody().TargetMappings.Single();
        var body = ValidBody() with { TargetMappings = [mapping, mapping] };

        var validation = new CreateComputeProfileRequestValidator()
            .Validate(new CreateComputeProfileRequest(body));

        Assert.Contains(validation.Errors, error =>
            error.PropertyName.EndsWith("TargetMappings", StringComparison.Ordinal) &&
            error.ErrorMessage.Contains("one mapping", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("999")]
    [InlineData("invalid")]
    public void Enum_names_in_string_request_fields_reject_numeric_and_unknown_values(string value)
    {
        var body = ValidBody() with { Architecture = value };
        Assert.Contains(new CreateComputeProfileRequestValidator().Validate(new CreateComputeProfileRequest(body)).Errors,
            error => error.PropertyName == "Body.Architecture");

        var affinity = new ComputeNodeAffinityExpressionRequestBody("example/key", value, ["value"]);
        Assert.Contains(new ComputeNodeAffinityExpressionRequestBodyValidator().Validate(affinity).Errors,
            error => error.PropertyName == "Operator");

        var invalidOperator = new ComputeTolerationRequestBody("example/key", value, null, "NoSchedule", null);
        Assert.Contains(new ComputeTolerationRequestBodyValidator().Validate(invalidOperator).Errors,
            error => error.PropertyName == "Operator");
        var invalidEffect = invalidOperator with { Operator = "Exists", Effect = value };
        Assert.Contains(new ComputeTolerationRequestBodyValidator().Validate(invalidEffect).Errors,
            error => error.PropertyName == "Effect");
    }

    private static CreateComputeProfileRequestBody ValidBody() =>
        new("test-profile",
            "Default",
            string.Empty,
            new ComputeResourceRequirementsRequestBody(
                new ComputeResourceValuesRequestBody(250, 256),
                new ComputeResourceValuesRequestBody(1000, 1024)),
            "Any",
            [],
            ["pole/web-service"],
            new ComputeProfileTenantAccessRequestBody("all-tenants", []),
            [
                new ComputeProfileTargetMappingRequestBody(
                    "507f1f77bcf86cd799439071",
                    [],
                    [],
                    [],
                    [],
                    null)
            ],
            IsEnabled: true,
            IsDefault: true);
}
