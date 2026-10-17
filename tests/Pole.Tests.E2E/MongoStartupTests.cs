using System.Text.Json;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class MongoStartupTests
{
    [Theory]
    [InlineData("Address already in use", "address-in-use")]
    [InlineData("Permission denied", "permission-denied")]
    [InlineData("No space left on device", "disk-full")]
    [InlineData("Cannot assign requested address", "address-unavailable")]
    [InlineData("sensitive native payload", "unclassified")]
    public void Native_errors_expose_only_numeric_codes_and_known_categories(string message, string category)
    {
        var line = JsonSerializer.Serialize(new
        {
            s = "E",
            id = 20568,
            msg = "private message",
            context = "private context",
            attr = new { error = new { code = 9001, codeName = "private code name", errmsg = message } }
        });

        var error = Assert.IsType<MongoStartupError>(MongoStartupError.Parse(line));

        Assert.Equal(20568, error.EventId);
        Assert.Equal(9001, error.Code);
        Assert.Equal(category, error.Cause);
        Assert.Equal($"MongoDB error: event=20568, code=9001, cause={category}", error.ToString());
    }

    [Fact]
    public void String_errors_and_missing_native_codes_are_supported_without_exposing_the_payload()
    {
        var error = MongoStartupError.Parse("""{"s":"F","id":23024,"attr":{"error":"Permission denied: secret-path"}}""");

        Assert.Equal("MongoDB error: event=23024, code=none, cause=permission-denied", error?.ToString());
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("{\"s\":\"I\",\"id\":1}")]
    [InlineData("{\"s\":false,\"id\":1}")]
    [InlineData("{\"s\":\"E\",\"id\":\"secret\"}")]
    [InlineData("{\"s\":\"E\",\"id\":9223372036854775808}")]
    public void Non_errors_or_malformed_frames_do_not_replace_the_original_startup_failure(string line) =>
        Assert.Null(MongoStartupError.Parse(line));

    [Fact]
    public void Oversized_native_lines_are_ignored() =>
        Assert.Null(MongoStartupError.Parse(new string('x', 16_385)));

    [Fact]
    public async Task Running_mongo_that_exits_while_the_API_is_waiting_fails_startup_promptly()
    {
        var api = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mongoExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startup = PoleE2EFixture.WaitForStartupAsync(Task.CompletedTask, api.Task, mongoExited.Task);
        Assert.False(startup.IsCompleted);

        mongoExited.SetResult();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => startup.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("MongoDB exited before the API was ready.", error.Message);
        Assert.False(api.Task.IsCompleted);
    }

    [Fact]
    public async Task Healthy_API_completes_startup_while_the_mongo_terminal_watcher_is_still_pending()
    {
        var mongoExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await PoleE2EFixture.WaitForStartupAsync(Task.CompletedTask, Task.CompletedTask, mongoExited.Task)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(mongoExited.Task.IsCompleted);
    }

    [Fact]
    public async Task Original_startup_failure_is_preserved_while_dependencies_are_still_pending()
    {
        var failure = new InvalidOperationException("controlled startup failure");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PoleE2EFixture.WaitForStartupAsync(Task.FromException(failure), pending.Task, pending.Task)
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Same(failure, error);
    }
}
