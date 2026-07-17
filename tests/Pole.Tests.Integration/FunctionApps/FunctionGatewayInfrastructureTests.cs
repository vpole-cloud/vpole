using Microsoft.AspNetCore.Http;
using Pole.Features.Functions.Runtime.Gateway;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionGatewayInfrastructureTests
{
    [Fact]
    public void Options_reject_overlapping_listener_ports()
    {
        var options = new FunctionGatewayOptions { ControlPort = 8081, PublicPort = 8081 };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("distinct", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(8081, "/api/v1/function-apps", false)]
    [InlineData(8082, "/health", false)]
    [InlineData(8081, "/functions/orders", true)]
    [InlineData(8082, "/functions/orders/items", true)]
    public async Task Listener_isolation_allows_only_function_paths_on_function_ports(
        int port,
        string path,
        bool shouldContinue)
    {
        var nextCalled = false;
        var middleware = new FunctionListenerIsolationMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new FunctionGatewayOptions());
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = port;
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        Assert.Equal(shouldContinue, nextCalled);
        if (!shouldContinue)
            Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public void Pending_admission_is_released_exactly_once()
    {
        var options = new FunctionGatewayOptions
        {
            MaxPendingRequests = 1,
            MaxPendingRequestsPerRevision = 1
        };
        var limiter = new FunctionPendingAdmissionLimiter(options);
        var lease = limiter.TryAcquire("tenant/app/revision", 10);

        Assert.NotNull(lease);
        Assert.Null(limiter.TryAcquire("tenant/app/revision", 10));

        lease.Dispose();
        lease.Dispose();
        Assert.NotNull(limiter.TryAcquire("tenant/app/revision", 10));
    }
}
