using Microsoft.Extensions.DependencyInjection;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Server.Extensions;
using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class MediatorOutcomeHandlerTests
{
    [Fact]
    public void MediatorResultCarriesTheRequestOutcome()
    {
        MediatorResult<string> result = "created";

        var outcome = result switch
        {
            string value => value,
            PipelineRejected => throw new Xunit.Sdk.XunitException(
                "Expected a request outcome.")
        };

        Assert.Equal("created", outcome);
    }

    [Fact]
    public void MediatorResultAcceptsAPluginDefinedRejection()
    {
        MediatorResult<string> result = new PipelineRejected(
            new PluginPolicyRejected("plugin.policy.denied"));

        var reason = result switch
        {
            string => throw new Xunit.Sdk.XunitException(
                "Expected a pipeline rejection."),
            PipelineRejected rejected => rejected.Reason
        };

        var rejection = Assert.IsType<PluginPolicyRejected>(reason);
        Assert.Equal("plugin.policy.denied", rejection.Code);
    }

    [Fact]
    public void PipelineRejectionRequiresATypedReason()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PipelineRejected(null!));
    }

    [Fact]
    public void LegacyResultRejectionRequiresAFailure()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new LegacyResultRejected(null!));
    }

    [Fact]
    public async Task RegistrationScanner_DiscoversOutcomeHandler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPoleMediator(typeof(MediatorOutcomeHandlerTests).Assembly);
        await using var provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<
            IMediatorOutcomeHandler<OutcomeRequest, string>>();

        Assert.IsType<OutcomeHandler>(handler);
    }

    [Fact]
    public async Task OutcomeHandler_ReturnsMediatorOutcomeThroughTheLegacyPipeline()
    {
        var handler = new OutcomeHandler();
        await using var provider = CreateProvider(handler);
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new OutcomeRequest("created"));

        var outcome = result switch
        {
            string value => value,
            PipelineRejected => throw new Xunit.Sdk.XunitException(
                "Expected a request outcome.")
        };

        Assert.Equal("created", outcome);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task LegacyPipelineFailureBecomesATypedMediatorRejection()
    {
        var handler = new OutcomeHandler();
        await using var provider = CreateProvider(handler, shortCircuit: true);
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new OutcomeRequest("not-reached"));

        var failure = result switch
        {
            string => throw new Xunit.Sdk.XunitException(
                "Expected a pipeline rejection."),
            PipelineRejected { Reason: LegacyResultRejected legacy } => legacy.Failure,
            PipelineRejected rejected => throw new Xunit.Sdk.XunitException(
                $"Expected a legacy rejection, but received '{rejected.Reason.GetType().FullName}'.")
        };

        Assert.IsType<ForbiddenFailure>(failure);
        Assert.Equal(0, handler.Calls);
    }

    private static ServiceProvider CreateProvider(
        OutcomeHandler handler,
        bool shortCircuit = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMediator, Pole.Mediator.Mediator>();
        services.AddSingleton<IMediatorOutcomeHandler<OutcomeRequest, string>>(handler);
        services.AddSingleton<IPipelineBehavior<OutcomeRequest, string>>(
            new ShortCircuitBehavior(shortCircuit));
        return services.BuildServiceProvider();
    }

    public sealed record OutcomeRequest(string Outcome) : IMediatorRequest<string>;

    private sealed record PluginPolicyRejected(string Code)
        : IMediatorRejection;

    public sealed class OutcomeHandler
        : IMediatorOutcomeHandler<OutcomeRequest, string>
    {
        public int Calls { get; private set; }

        public Task<string> HandleAsync(
            OutcomeRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(request.Outcome);
        }
    }

    public sealed class ShortCircuitBehavior(bool shortCircuit)
        : IPipelineBehavior<OutcomeRequest, string>
    {
        public Task<Result<string>> Handle(
            OutcomeRequest request,
            RequestHandlerDelegate<string> next,
            CancellationToken cancellationToken) =>
            shortCircuit
                ? Task.FromResult<Result<string>>(
                    Failures.Forbidden("Blocked for the test.", "Test"))
                : next();
    }
}
