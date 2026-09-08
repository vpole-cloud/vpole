using Microsoft.Extensions.DependencyInjection;
using Pole.Mediator;
using Pole.Server.Extensions;
using Pole.Platform.Hosting;
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
    public void RequestValidationRejectionRequiresFieldErrors()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RequestValidationRejected(null!));
        Assert.Throws<ArgumentException>(() =>
            new RequestValidationRejected(
                new Dictionary<string, string[]>()));
        Assert.Throws<ArgumentException>(() =>
            new RequestValidationRejected(
                new Dictionary<string, string[]>
                {
                    ["Body.Name"] = []
                }));
    }

    [Fact]
    public void RequestValidationRejectionOwnsAnImmutableSnapshot()
    {
        var messages = new[] { "Name is required." };
        var errors = new Dictionary<string, string[]>
        {
            ["Body.Name"] = messages
        };

        var rejection = new RequestValidationRejected(errors);
        messages[0] = "Changed.";
        errors["Body.Name"] = ["Replaced."];

        Assert.Equal(
            "Name is required.",
            Assert.Single(rejection.Errors["Body.Name"]));
        Assert.IsAssignableFrom<IReadOnlyList<string>>(
            rejection.Errors["Body.Name"]);
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
    public async Task OutcomeHandler_ReturnsOutcomeThroughMediatorPipeline()
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
    public async Task PluginBehavior_CanRejectBeforeOutcomeHandler()
    {
        var handler = new OutcomeHandler();
        await using var provider = CreateProvider(handler, shortCircuit: true);
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new OutcomeRequest("not-reached"));

        var reason = result switch
        {
            string => throw new Xunit.Sdk.XunitException(
                "Expected a pipeline rejection."),
            PipelineRejected rejected => rejected.Reason
        };

        var rejection = Assert.IsType<PluginPolicyRejected>(reason);
        Assert.Equal("plugin.policy.denied", rejection.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task PipelineBehaviors_PreserveRegistrationOrder()
    {
        var trace = new List<string>();
        var handler = new OutcomeHandler(trace);
        var services = new ServiceCollection();
        services.AddSingleton<IMediator, Pole.Mediator.Mediator>();
        services.AddSingleton<IMediatorOutcomeHandler<OutcomeRequest, string>>(handler);
        services.AddSingleton<IPipelineBehavior<OutcomeRequest, string>>(
            new RecordingBehavior("outer", trace));
        services.AddSingleton<IPipelineBehavior<OutcomeRequest, string>>(
            new RecordingBehavior("inner", trace));
        await using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new OutcomeRequest("created"));

        var outcome = result.Match(
            value => value,
            _ => throw new Xunit.Sdk.XunitException("Expected a request outcome."));
        Assert.Equal("created", outcome);
        Assert.Equal(
            ["outer:before", "inner:before", "handler", "inner:after", "outer:after"],
            trace);
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

    public sealed class OutcomeHandler(ICollection<string>? trace = null)
        : IMediatorOutcomeHandler<OutcomeRequest, string>
    {
        public int Calls { get; private set; }

        public Task<string> HandleAsync(
            OutcomeRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            trace?.Add("handler");
            return Task.FromResult(request.Outcome);
        }
    }

    public sealed class ShortCircuitBehavior(bool shortCircuit)
        : IPipelineBehavior<OutcomeRequest, string>
    {
        public Task<MediatorResult<string>> Handle(
            OutcomeRequest request,
            RequestHandlerDelegate<string> next,
            CancellationToken cancellationToken) =>
            shortCircuit
                ? Task.FromResult<MediatorResult<string>>(
                    new PipelineRejected(
                        new PluginPolicyRejected("plugin.policy.denied")))
                : next();
    }

    public sealed class RecordingBehavior(
        string name,
        ICollection<string> trace)
        : IPipelineBehavior<OutcomeRequest, string>
    {
        public async Task<MediatorResult<string>> Handle(
            OutcomeRequest request,
            RequestHandlerDelegate<string> next,
            CancellationToken cancellationToken)
        {
            trace.Add($"{name}:before");
            var result = await next();
            trace.Add($"{name}:after");
            return result;
        }
    }
}
