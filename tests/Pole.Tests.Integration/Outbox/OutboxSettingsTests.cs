using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pole.Features.Outbox.Application;
using Pole.Server.Extensions;
using Pole.Platform.Hosting;
using Xunit;

namespace Pole.Tests.Integration.Outbox;

public sealed class OutboxSettingsTests
{
    private readonly OutboxSettingsValidator _validator = new();

    [Fact]
    public void Defaults_AreSafeAndValid()
    {
        var settings = new OutboxSettings();

        var validation = _validator.Validate(null, settings);

        Assert.False(validation.Failed);
        Assert.Equal(100, settings.BatchSize);
        Assert.Equal("* * * * *", settings.ProcessingCronExpression);
        Assert.Null(settings.ProcessingTriggerInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), settings.InitialRetryDelay);
        Assert.Equal(TimeSpan.FromMinutes(5), settings.MaximumRetryDelay);
        Assert.Equal(7, settings.PublishedRetentionDays);
        Assert.Equal(30, settings.AbandonedRetentionDays);
        Assert.Equal(500, settings.CleanupBatchSize);
    }

    [Theory]
    [InlineData("* * * * *")]
    [InlineData("0 0-23/2 * * *")]
    public void Validator_AcceptsSupportedFiveFieldCronSyntax(string expression)
    {
        var settings = new OutboxSettings
        {
            ProcessingCronExpression = expression
        };

        var validation = _validator.Validate(null, settings);

        Assert.False(validation.Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-cron")]
    [InlineData("* * *")]
    [InlineData("*/15 * * * * *")]
    [InlineData("60 * * * * *")]
    [InlineData("*/0 * * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("10-5 * * * *")]
    public void Validator_RejectsInvalidCronSyntax(string expression)
    {
        var settings = new OutboxSettings
        {
            ProcessingCronExpression = expression
        };

        var validation = _validator.Validate(null, settings);

        Assert.True(validation.Failed);
        Assert.Contains(
            validation.Failures,
            failure => failure.Contains(
                nameof(OutboxSettings.ProcessingCronExpression),
                StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsNonPositiveValues()
    {
        var settings = new OutboxSettings
        {
            BatchSize = 0,
            ProcessingTriggerInterval = TimeSpan.Zero,
            InitialRetryDelay = TimeSpan.Zero,
            MaximumRetryDelay = TimeSpan.FromSeconds(-1),
            PublishedRetentionDays = 0,
            AbandonedRetentionDays = -1,
            CleanupBatchSize = 0
        };

        var validation = _validator.Validate(null, settings);

        Assert.True(validation.Failed);
        AssertFailureFor(validation.Failures, nameof(OutboxSettings.BatchSize));
        AssertFailureFor(validation.Failures, nameof(OutboxSettings.ProcessingTriggerInterval));
        AssertFailureFor(validation.Failures, nameof(OutboxSettings.InitialRetryDelay));
        AssertFailureFor(validation.Failures, nameof(OutboxSettings.MaximumRetryDelay));
        AssertFailureFor(validation.Failures, nameof(OutboxSettings.PublishedRetentionDays));
        AssertFailureFor(validation.Failures, nameof(OutboxSettings.AbandonedRetentionDays));
        AssertFailureFor(validation.Failures, nameof(OutboxSettings.CleanupBatchSize));
    }

    [Fact]
    public void Validator_RejectsInconsistentRetryAndRetentionRanges()
    {
        var settings = new OutboxSettings
        {
            InitialRetryDelay = TimeSpan.FromMinutes(2),
            MaximumRetryDelay = TimeSpan.FromMinutes(1),
            PublishedRetentionDays = 10,
            AbandonedRetentionDays = 9
        };

        var validation = _validator.Validate(null, settings);

        Assert.True(validation.Failed);
        AssertFailureFor(validation.Failures, nameof(OutboxSettings.MaximumRetryDelay));
        AssertFailureFor(validation.Failures, nameof(OutboxSettings.AbandonedRetentionDays));
    }

    [Fact]
    public async Task ProcessingTriggerInterval_DispatchesWithoutWaitingForTheCronSchedule()
    {
        var trigger = new RecordingOutboxProcessingTrigger();
        using var services = new ServiceCollection()
            .AddSingleton<IOutboxProcessingTrigger>(trigger)
            .BuildServiceProvider();
        using var service = new OutboxProcessingTriggerService(
            Options.Create(new OutboxSettings
            {
                ProcessingTriggerInterval = TimeSpan.FromMilliseconds(10)
            }),
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<OutboxProcessingTriggerService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await trigger.FirstTrigger.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.True(trigger.Count >= 1);
    }

    [Fact]
    public void AddOutboxFeature_WiresValidationIntoOptionsPipeline()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:ProcessingCronExpression"] = "not-a-cron"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOutboxFeature(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<OutboxSettings>>().Value);
    }

    private static void AssertFailureFor(
        IEnumerable<string> failures,
        string propertyName) =>
        Assert.Contains(
            failures,
            failure => failure.Contains(propertyName, StringComparison.Ordinal));

    private sealed class RecordingOutboxProcessingTrigger : IOutboxProcessingTrigger
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);
        public TaskCompletionSource FirstTrigger { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task TriggerAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            FirstTrigger.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
