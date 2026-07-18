using Pole.Features.Functions.Runtime.Coordination;
using Pole.Features.Functions.Runtime.Gateway;
using Pole.Features.Functions.Runtime.Scaling;
using Pole.Features.Functions.Runtime.Timers;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionRuntimeOptionsValidationTests
{
    [Fact]
    public void Default_options_are_valid()
    {
        Assert.True(new FunctionGatewayOptionsValidator()
            .Validate(null, new FunctionGatewayOptions()).Succeeded);
        Assert.True(new FunctionScalerOptionsValidator()
            .Validate(null, new FunctionScalerOptions()).Succeeded);
        Assert.True(new FunctionTimerOptionsValidator()
            .Validate(null, new FunctionTimerOptions()).Succeeded);
        Assert.True(new FunctionCoordinationOptionsValidator()
            .Validate(null, new FunctionCoordinationOptions()).Succeeded);
    }

    [Fact]
    public void Gateway_validator_applies_property_ranges()
    {
        var options = new FunctionGatewayOptions { AgentLongPollMilliseconds = 30_001 };

        var result = new FunctionGatewayOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains(nameof(options.AgentLongPollMilliseconds), StringComparison.Ordinal));
    }

    [Fact]
    public void Gateway_validator_bounds_request_body_timeout()
    {
        var options = new FunctionGatewayOptions { RequestBodyTimeoutSeconds = 301 };

        var result = new FunctionGatewayOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains(nameof(options.RequestBodyTimeoutSeconds), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(101, 100, 16, 64, 200, 100, "MaxPendingRequestsPerRevision")]
    [InlineData(100, 100, 65, 64, 200, 100, "MaxPendingBytesPerRevision")]
    [InlineData(100, 100, 16, 64, 99, 100, "PublicBurst")]
    public void Gateway_validator_applies_cross_property_rules(
        int revisionRequests,
        int totalRequests,
        long revisionBytes,
        long totalBytes,
        int burst,
        int requestsPerSecond,
        string expectedFailure)
    {
        var options = new FunctionGatewayOptions
        {
            MaxPendingRequestsPerRevision = revisionRequests,
            MaxPendingRequests = totalRequests,
            MaxPendingBytesPerRevision = revisionBytes,
            MaxPendingBytes = totalBytes,
            PublicBurst = burst,
            PublicRequestsPerSecond = requestsPerSecond
        };

        var result = new FunctionGatewayOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains(expectedFailure, StringComparison.Ordinal));
    }

    [Fact]
    public void Scaler_validator_applies_property_ranges()
    {
        var options = new FunctionScalerOptions { AgentStatusTimeoutSeconds = 31 };

        var result = new FunctionScalerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains(nameof(options.AgentStatusTimeoutSeconds), StringComparison.Ordinal));
    }

    [Fact]
    public void Timer_validator_applies_property_ranges()
    {
        var options = new FunctionTimerOptions { BatchSize = 501 };

        var result = new FunctionTimerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains(nameof(options.BatchSize), StringComparison.Ordinal));
    }

    [Fact]
    public void Timer_validator_requires_dispatch_lease_to_exceed_execution_timeout()
    {
        var options = new FunctionTimerOptions
        {
            DispatchLeaseSeconds = 900,
            ExecutionTimeoutSeconds = 900
        };

        var result = new FunctionTimerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains(nameof(options.DispatchLeaseSeconds), StringComparison.Ordinal));
    }

    [Fact]
    public void Timer_validator_requires_dispatch_lease_to_cover_maximum_cold_activation()
    {
        var options = new FunctionTimerOptions
        {
            DispatchLeaseSeconds = 120,
            ExecutionTimeoutSeconds = 10
        };

        var result = new FunctionTimerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains("maximum Function activation timeout", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(150, 150, 300, "AdmissionLeaseSeconds")]
    [InlineData(300, 150, 299, "RecoveryGraceSeconds")]
    [InlineData(300, 129, 300, "ActivationLeaseSeconds")]
    public void Coordination_validator_enforces_lease_ordering(
        int admissionSeconds,
        int activationSeconds,
        int recoverySeconds,
        string expectedFailure)
    {
        var options = new FunctionCoordinationOptions
        {
            AdmissionLeaseSeconds = admissionSeconds,
            ActivationLeaseSeconds = activationSeconds,
            RecoveryGraceSeconds = recoverySeconds
        };

        var result = new FunctionCoordinationOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains(expectedFailure, StringComparison.Ordinal));
    }

    [Fact]
    public void Coordination_validator_bounds_grain_call_timeout()
    {
        var options = new FunctionCoordinationOptions { CallTimeoutSeconds = 31 };

        var result = new FunctionCoordinationOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains(nameof(options.CallTimeoutSeconds), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(5, 7, 300)]
    [InlineData(5, 300, 300)]
    public void Coordination_validator_bounds_initial_admission_lease(
        int callTimeoutSeconds,
        int initialLeaseSeconds,
        int admissionLeaseSeconds)
    {
        var options = new FunctionCoordinationOptions
        {
            CallTimeoutSeconds = callTimeoutSeconds,
            InitialAdmissionLeaseSeconds = initialLeaseSeconds,
            AdmissionLeaseSeconds = admissionLeaseSeconds
        };

        var result = new FunctionCoordinationOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains(nameof(options.InitialAdmissionLeaseSeconds), StringComparison.Ordinal));
    }
}
