using Microsoft.Extensions.DependencyInjection;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Server.Extensions;
using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class MediatorOutcomeHandlerTests
{
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
    public async Task OutcomeHandler_IsWrappedForTheExistingPipeline()
    {
        var handler = new OutcomeHandler();
        await using var provider = CreateProvider(handler);
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new OutcomeRequest("created"));

        Assert.True(result.TryGetValue(out var outcome, out _));
        Assert.Equal("created", outcome);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Pipeline_CanShortCircuitBeforeOutcomeHandler()
    {
        var handler = new OutcomeHandler();
        await using var provider = CreateProvider(handler, shortCircuit: true);
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new OutcomeRequest("not-reached"));

        Assert.False(result.TryGetValue(out _, out var failure));
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
