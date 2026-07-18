using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using k8s.Models;
using Microsoft.AspNetCore.Http;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Activation;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Gateway;
using Pole.Features.Functions.Runtime.Metering;
using Pole.Functions.Protocol;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionHttpGatewayTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Warm_request_uses_ready_snapshot_without_activation()
    {
        var fixture = new GatewayFixture();

        var context = fixture.CreateContext();
        await fixture.Gateway.InvokeAsync(context, "orders", "items/42");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("ok", await ReadBodyAsync(context));
        Assert.Equal(0, fixture.Activator.Calls);
        Assert.Single(fixture.Agent.ReservedEndpoints);
        Assert.Single(fixture.Agent.ExecutedEndpoints);
        Assert.Single(fixture.Agent.ObservedEndpoints);
        Assert.Equal(
            [
                FunctionMeterEventPhase.Admitted,
                FunctionMeterEventPhase.Started,
                FunctionMeterEventPhase.Completed
            ],
            fixture.Meter.Events.Select(meter => meter.Phase));
        Assert.All(fixture.Meter.Events, meter => Assert.Equal(FunctionMeterTriggerKind.Http, meter.TriggerKind));
    }

    [Fact]
    public async Task Post_start_meter_stall_is_bounded_and_preserves_user_response()
    {
        var meter = new StallingPostStartMeterWriter();
        var options = GatewayFixture.CreateOptions();
        options.PostStartMeterWriteTimeoutMilliseconds = 10;
        var fixture = new GatewayFixture(options: options, meterWriter: meter);

        var context = fixture.CreateContext();
        await fixture.Gateway.InvokeAsync(context, "orders", "items/42");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("ok", await ReadBodyAsync(context));
        Assert.Equal(1, meter.RequiredWrites);
        Assert.Equal(2, meter.CancelledPostStartWrites);
    }

    [Fact]
    public async Task Cold_request_activates_revision_and_dispatches_to_activated_endpoint()
    {
        var coldEndpoint = new FunctionEndpoint("10.0.0.8", 8080, "pod-cold");
        var fixture = new GatewayFixture(
            registrySnapshot: GatewayFixture.Snapshot([]),
            activationSnapshot: GatewayFixture.Snapshot([coldEndpoint]));

        var context = fixture.CreateContext();
        await fixture.Gateway.InvokeAsync(context, "orders", "items/42");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1, fixture.Activator.Calls);
        Assert.Equal(coldEndpoint, Assert.Single(fixture.Agent.ExecutedEndpoints));
    }

    [Fact]
    public async Task Admission_rejection_returns_429_without_activation_or_agent_calls()
    {
        var fixture = new GatewayFixture(
            registrySnapshot: GatewayFixture.Snapshot([]),
            admission: new RejectingAdmissionLimiter());

        var context = fixture.CreateContext();
        await fixture.Gateway.InvokeAsync(context, "orders", "items/42");

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Equal("pending_limit_exceeded", await ReadErrorCodeAsync(context));
        Assert.Equal(0, fixture.Activator.Calls);
        Assert.Empty(fixture.Agent.ReservedEndpoints);
    }

    [Fact]
    public async Task Capacity_rejection_before_start_reroutes_same_attempt_to_next_endpoint()
    {
        var first = new FunctionEndpoint("10.0.0.1", 8080, "pod-1");
        var second = new FunctionEndpoint("10.0.0.2", 8080, "pod-2");
        var agent = new RecordingAgentClient
        {
            ReserveHandler = (endpoint, request) => endpoint == first
                ? new FunctionAgentProtocolResponse<ReserveFunctionAttemptResponse>(
                    HttpStatusCode.TooManyRequests,
                    null,
                    new FunctionProtocolError(PoleFunctionProtocolErrors.CapacityExhausted, "full", true))
                : RecordingAgentClient.SuccessfulReservation(request)
        };
        var fixture = new GatewayFixture(
            registrySnapshot: GatewayFixture.Snapshot([first, second]),
            agent: agent);

        var context = fixture.CreateContext();
        await fixture.Gateway.InvokeAsync(context, "orders", "items/42");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal([first, second], agent.ReservedEndpoints);
        Assert.Equal(second, Assert.Single(agent.ExecutedEndpoints));
        Assert.Equal(second, Assert.Single(agent.ObservedEndpoints));
        Assert.Single(agent.ReserveRequests.Select(request => request.Attempt.AttemptId).Distinct());
    }

    [Fact]
    public async Task Ambiguous_execute_failure_observes_same_endpoint_without_rerouting()
    {
        var first = new FunctionEndpoint("10.0.0.1", 8080, "pod-1");
        var second = new FunctionEndpoint("10.0.0.2", 8080, "pod-2");
        var agent = new RecordingAgentClient
        {
            ExecuteHandler = (_, _, _) => throw new HttpRequestException("response was lost")
        };
        var fixture = new GatewayFixture(
            registrySnapshot: GatewayFixture.Snapshot([first, second]),
            agent: agent);

        var context = fixture.CreateContext();
        await fixture.Gateway.InvokeAsync(context, "orders", "items/42");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(first, Assert.Single(agent.ReservedEndpoints));
        Assert.Equal(first, Assert.Single(agent.ExecutedEndpoints));
        Assert.Equal(first, Assert.Single(agent.ObservedEndpoints));
    }

    [Fact]
    public async Task Handler_500_and_safe_headers_are_passed_through_while_hop_by_hop_headers_are_removed()
    {
        var agent = new RecordingAgentClient
        {
            CompletionResult = new HttpFunctionInvocationResult(
                StatusCodes.Status500InternalServerError,
                [
                    new FunctionHttpHeader("Connection", ["X-Private"]),
                    new FunctionHttpHeader("X-Private", ["secret"]),
                    new FunctionHttpHeader("Transfer-Encoding", ["chunked"]),
                    new FunctionHttpHeader("Content-Length", ["7"]),
                    new FunctionHttpHeader("X-Visible", ["yes"])
                ],
                Encoding.UTF8.GetBytes("failure"))
        };
        var fixture = new GatewayFixture(agent: agent);

        var context = fixture.CreateContext();
        await fixture.Gateway.InvokeAsync(context, "orders", "items/42");

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("failure", await ReadBodyAsync(context));
        Assert.Equal("yes", context.Response.Headers["X-Visible"]);
        Assert.False(context.Response.Headers.ContainsKey("Connection"));
        Assert.False(context.Response.Headers.ContainsKey("X-Private"));
        Assert.False(context.Response.Headers.ContainsKey("Transfer-Encoding"));
        Assert.False(context.Response.Headers.ContainsKey("Content-Length"));
    }

    [Theory]
    [InlineData("HEAD", StatusCodes.Status200OK)]
    [InlineData("GET", StatusCodes.Status204NoContent)]
    [InlineData("GET", StatusCodes.Status304NotModified)]
    public async Task Response_body_is_suppressed_when_http_semantics_forbid_it(string method, int statusCode)
    {
        var agent = new RecordingAgentClient
        {
            CompletionResult = new HttpFunctionInvocationResult(
                statusCode,
                [],
                Encoding.UTF8.GetBytes("must-not-be-written"))
        };
        var fixture = new GatewayFixture(agent: agent);

        var context = fixture.CreateContext(method);
        await fixture.Gateway.InvokeAsync(context, "orders", "items/42");

        Assert.Equal(statusCode, context.Response.StatusCode);
        Assert.Equal(string.Empty, await ReadBodyAsync(context));
    }

    [Fact]
    public async Task Oversized_handler_response_is_rejected_as_invalid_agent_output()
    {
        var options = GatewayFixture.CreateOptions();
        options.MaxResponseBodyBytes = 3;
        var agent = new RecordingAgentClient
        {
            CompletionResult = new HttpFunctionInvocationResult(
                StatusCodes.Status200OK,
                [],
                Encoding.UTF8.GetBytes("four"))
        };
        var fixture = new GatewayFixture(agent: agent, options: options);

        var context = fixture.CreateContext();
        await fixture.Gateway.InvokeAsync(context, "orders", "items/42");

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal("invalid_handler_response", await ReadErrorCodeAsync(context));
    }

    [Theory]
    [InlineData(8081, FunctionExposure.Public)]
    [InlineData(8082, FunctionExposure.Internal)]
    public async Task Listener_port_selects_public_or_internal_route_exposure(
        int port,
        FunctionExposure expectedExposure)
    {
        var fixture = new GatewayFixture();

        var context = fixture.CreateContext(localPort: port);
        await fixture.Gateway.InvokeAsync(context, "orders", "items/42");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(expectedExposure, fixture.Resolver.LastExposure);
    }

    [Fact]
    public async Task Control_listener_does_not_resolve_or_dispatch_function_routes()
    {
        var fixture = new GatewayFixture();

        var context = fixture.CreateContext(localPort: fixture.Options.ControlPort);
        await fixture.Gateway.InvokeAsync(context, "orders", "items/42");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal(0, fixture.Resolver.Calls);
        Assert.Empty(fixture.Agent.ReservedEndpoints);
    }

    [Fact]
    public async Task Only_allowlisted_non_sensitive_request_headers_reach_the_handler()
    {
        var options = GatewayFixture.CreateOptions();
        options.ForwardedRequestHeaders = new HashSet<string>(
            [
                "X-Allowed", "Authorization", "Cookie", "Host", "X-Pole-Tenant-ID",
                "X-Forwarded-For", "Connection", "X-Connection-Nominated"
            ],
            StringComparer.OrdinalIgnoreCase);
        var fixture = new GatewayFixture(options: options);
        var context = fixture.CreateContext();
        context.Request.Headers["X-Allowed"] = "visible";
        context.Request.Headers.Authorization = "Bearer user-token";
        context.Request.Headers.Cookie = "session=secret";
        context.Request.Host = new HostString("functions.example");
        context.Request.Headers["X-Pole-Tenant-ID"] = "another-tenant";
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.10";
        context.Request.Headers.Connection = "X-Connection-Nominated";
        context.Request.Headers["X-Connection-Nominated"] = "remove-me";

        await fixture.Gateway.InvokeAsync(context, "orders", "items/42");

        var payload = Assert.IsType<HttpFunctionInvocationPayload>(fixture.Agent.ExecuteRequests.Single().Payload);
        var forwarded = payload.Headers.ToDictionary(header => header.Name, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("visible", Assert.Single(forwarded["X-Allowed"].Values));
        Assert.DoesNotContain("Authorization", forwarded.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie", forwarded.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host", forwarded.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Pole-Tenant-ID", forwarded.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Forwarded-For", forwarded.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Connection", forwarded.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Connection-Nominated", forwarded.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reservation_attempt_identity_mismatch_is_rejected_without_execution()
    {
        var agent = new RecordingAgentClient
        {
            ReserveHandler = (_, request) => RecordingAgentClient.SuccessfulReservation(request) with
            {
                Value = RecordingAgentClient.SuccessfulReservation(request).Value! with
                {
                    AttemptId = "different-attempt"
                }
            }
        };
        var fixture = new GatewayFixture(agent: agent);

        var context = fixture.CreateContext();
        await fixture.Gateway.InvokeAsync(context, "orders", "items/42");

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal("agent_attempt_mismatch", await ReadErrorCodeAsync(context));
        Assert.Empty(agent.ExecutedEndpoints);
    }

    [Fact]
    public async Task Observation_agent_identity_mismatch_is_rejected_without_rerouting()
    {
        var agent = new RecordingAgentClient
        {
            ObservedAgentInstanceId = "different-pod"
        };
        var fixture = new GatewayFixture(agent: agent);

        var context = fixture.CreateContext();
        await fixture.Gateway.InvokeAsync(context, "orders", "items/42");

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal("agent_identity_mismatch", await ReadErrorCodeAsync(context));
        Assert.Single(agent.ReservedEndpoints);
        Assert.Single(agent.ExecutedEndpoints);
        Assert.Single(agent.ObservedEndpoints);
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static async Task<string> ReadErrorCodeAsync(HttpContext context)
    {
        var json = await ReadBodyAsync(context);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private sealed class GatewayFixture
    {
        private static readonly FunctionEndpoint DefaultEndpoint = new("10.0.0.1", 8080, "pod-1");

        public GatewayFixture(
            FunctionEndpointSnapshot? registrySnapshot = null,
            FunctionEndpointSnapshot? activationSnapshot = null,
            RecordingAgentClient? agent = null,
            IFunctionPendingAdmissionLimiter? admission = null,
            FunctionGatewayOptions? options = null,
            IFunctionMeterWriter? meterWriter = null)
        {
            Options = options ?? CreateOptions();
            var resolved = CreateResolvedRoute();
            Resolver = new RecordingRouteResolver(resolved);
            Registry = new StubEndpointRegistry(registrySnapshot ?? Snapshot([DefaultEndpoint]));
            Activator = new RecordingActivator(activationSnapshot ?? Snapshot([DefaultEndpoint]));
            Agent = agent ?? new RecordingAgentClient();
            Meter = new RecordingMeterWriter();
            Gateway = new FunctionHttpGateway(
                Resolver,
                Registry,
                new SnapshotOrderEndpointSelector(),
                Activator,
                Agent,
                admission ?? new FunctionPendingAdmissionLimiter(Options),
                new AllowingPublicRateLimiter(),
                meterWriter ?? Meter,
                Options,
                TimeProvider.System);
        }

        public FunctionGatewayOptions Options { get; }
        public RecordingRouteResolver Resolver { get; }
        public StubEndpointRegistry Registry { get; }
        public RecordingActivator Activator { get; }
        public RecordingAgentClient Agent { get; }
        public RecordingMeterWriter Meter { get; }
        public FunctionHttpGateway Gateway { get; }

        public DefaultHttpContext CreateContext(
            string method = "POST",
            int? localPort = null)
        {
            var context = new DefaultHttpContext();
            context.Connection.LocalPort = localPort ?? Options.PublicPort;
            context.Request.Method = method;
            context.Request.Path = "/functions/orders/items/42";
            context.Request.QueryString = new QueryString("?expand=true");
            context.Request.ContentLength = 0;
            context.Response.Body = new MemoryStream();
            return context;
        }

        public static FunctionGatewayOptions CreateOptions() => new()
        {
            ControlPort = 8080,
            PublicPort = 8081,
            InternalPort = 8082,
            ExecutionTimeoutSeconds = 10,
            AgentLongPollMilliseconds = 100
        };

        public static FunctionEndpointSnapshot Snapshot(IEnumerable<FunctionEndpoint> endpoints) => new(
            new FunctionServiceKey("pole-tenant-rg", "function-orders-agent"),
            endpoints.ToImmutableArray(),
            Now);

        private static ResolvedFunctionHttpRoute CreateResolvedRoute()
        {
            var spec = new FunctionRevisionSpecInput(
                new FunctionImage("registry.example", "team/functions", "v1"),
                PoleFunctionProtocol.Version,
                new FunctionConfiguration(
                    ImmutableDictionary<string, string>.Empty,
                    ImmutableDictionary<string, string>.Empty),
                new FunctionResources(
                    new FunctionResourceValues(100, 128),
                    new FunctionResourceValues(500, 512)),
                new FunctionScalingPolicy(0, 5, 4, 8, 30, 30),
                [
                    new FunctionHandlerSpec(
                        "webhook",
                        4,
                        new HttpFunctionTriggerSpec(
                            string.Empty,
                            "items/{id}",
                            ["GET", "HEAD", "POST"],
                            FunctionExposure.Public))
                ]);
            var created = FunctionAppFactory.Create(
                "tenant-1",
                "group-1",
                "pole-tenant-rg",
                "orders",
                "orders",
                spec,
                [],
                Now);
            const string appId = "507f1f77bcf86cd799439011";
            const string revisionId = "507f1f77bcf86cd799439012";
            var app = created.FunctionApp with
            {
                Id = appId,
                ActiveRevisionId = revisionId,
                PendingRevisionId = null,
                Status = FunctionAppStatus.Ready
            };
            var revision = (created.Revision with
            {
                Id = revisionId,
                FunctionAppId = appId
            }).Activate(Now);
            var route = new FunctionHttpRouteBinding(
                "trigger-1",
                "webhook",
                "items/{id}",
                ["GET", "HEAD", "POST"],
                FunctionExposure.Public);
            return new ResolvedFunctionHttpRoute(app, revision, route);
        }
    }

    private sealed class RecordingRouteResolver(ResolvedFunctionHttpRoute route) : IFunctionRuntimeRouteResolver
    {
        public int Calls { get; private set; }
        public FunctionExposure? LastExposure { get; private set; }

        public Task<ResolvedFunctionHttpRoute?> ResolveAsync(
            string functionRoute,
            string handlerPath,
            string method,
            FunctionExposure exposure,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastExposure = exposure;
            return Task.FromResult<ResolvedFunctionHttpRoute?>(route);
        }
    }

    private sealed class StubEndpointRegistry(FunctionEndpointSnapshot snapshot) : IFunctionEndpointRegistry
    {
        public FunctionEndpointSnapshot GetSnapshot(FunctionServiceKey service) => snapshot;
        public void ReplaceAll(IEnumerable<V1EndpointSlice> slices) => throw new NotSupportedException();
        public void ApplySlice(V1EndpointSlice slice) => throw new NotSupportedException();
        public void DeleteSlice(string @namespace, string sliceName) => throw new NotSupportedException();
    }

    private sealed class SnapshotOrderEndpointSelector : IFunctionEndpointSelector
    {
        public IReadOnlyList<FunctionEndpoint> Order(FunctionEndpointSnapshot snapshot) => snapshot.Endpoints;
    }

    private sealed class RecordingActivator(FunctionEndpointSnapshot snapshot) : IFunctionRevisionActivator
    {
        public int Calls { get; private set; }

        public Task<Result<FunctionEndpointSnapshot>> EnsureActiveAsync(
            FunctionApp app,
            FunctionRevision revision,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<Result<FunctionEndpointSnapshot>>(snapshot);
        }
    }

    private sealed class RejectingAdmissionLimiter : IFunctionPendingAdmissionLimiter
    {
        public FunctionPendingAdmissionLease? TryAcquire(
            string revisionId,
            string handlerName,
            long reservedBytes) => null;
        public FunctionPendingAdmissionSnapshot GetSnapshot(string revisionId) =>
            FunctionPendingAdmissionSnapshot.Empty;
        public FunctionScaleDownLease? TryBeginScaleDown(string revisionId) => null;
    }

    private sealed class AllowingPublicRateLimiter : IFunctionPublicRateLimiter
    {
        public bool TryAcquire(string functionRoute) => true;
    }

    private sealed class RecordingMeterWriter : IFunctionMeterWriter
    {
        public List<FunctionMeterEvent> Events { get; } = [];

        public Task WriteAsync(
            FunctionMeterEvent meterEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(meterEvent);
            return Task.CompletedTask;
        }

        public Task TryWriteAsync(
            FunctionMeterEvent meterEvent,
            CancellationToken cancellationToken = default) =>
            WriteAsync(meterEvent, cancellationToken);
    }

    private sealed class StallingPostStartMeterWriter : IFunctionMeterWriter
    {
        public int RequiredWrites { get; private set; }
        public int CancelledPostStartWrites { get; private set; }

        public Task WriteAsync(
            FunctionMeterEvent meterEvent,
            CancellationToken cancellationToken = default)
        {
            RequiredWrites++;
            return Task.CompletedTask;
        }

        public async Task TryWriteAsync(
            FunctionMeterEvent meterEvent,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancelledPostStartWrites++;
            }
        }
    }

    private sealed class RecordingAgentClient : IFunctionAgentClient
    {
        public List<FunctionEndpoint> ReservedEndpoints { get; } = [];
        public List<FunctionEndpoint> ExecutedEndpoints { get; } = [];
        public List<FunctionEndpoint> ObservedEndpoints { get; } = [];
        public List<ReserveFunctionAttemptRequest> ReserveRequests { get; } = [];
        public List<ExecuteFunctionAttemptRequest> ExecuteRequests { get; } = [];

        public Func<FunctionEndpoint, ReserveFunctionAttemptRequest,
            FunctionAgentProtocolResponse<ReserveFunctionAttemptResponse>>? ReserveHandler { get; init; }
        public Func<FunctionEndpoint, string, ExecuteFunctionAttemptRequest,
            FunctionAgentProtocolResponse<StartFunctionAttemptResponse>>? ExecuteHandler { get; init; }
        public Func<FunctionEndpoint, string,
            FunctionAgentProtocolResponse<FunctionAttemptStatusResponse>>? ObserveHandler { get; init; }
        public HttpFunctionInvocationResult CompletionResult { get; init; } =
            new(StatusCodes.Status200OK, [], Encoding.UTF8.GetBytes("ok"));
        public string? ObservedAgentInstanceId { get; init; }

        public Task<FunctionAgentProtocolResponse<FunctionAgentMetadataResponse>> GetMetadataAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FunctionAgentProtocolResponse<FunctionAgentStatusResponse>> GetStatusAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FunctionAgentProtocolResponse<ReserveFunctionAttemptResponse>> ReserveAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            ReserveFunctionAttemptRequest request,
            CancellationToken cancellationToken)
        {
            ReservedEndpoints.Add(endpoint);
            ReserveRequests.Add(request);
            return Task.FromResult(ReserveHandler?.Invoke(endpoint, request) ?? SuccessfulReservation(request));
        }

        public Task<FunctionAgentProtocolResponse<StartFunctionAttemptResponse>> ExecuteAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            string attemptId,
            ExecuteFunctionAttemptRequest request,
            CancellationToken cancellationToken)
        {
            ExecutedEndpoints.Add(endpoint);
            ExecuteRequests.Add(request);
            var response = ExecuteHandler?.Invoke(endpoint, attemptId, request) ??
                           new FunctionAgentProtocolResponse<StartFunctionAttemptResponse>(
                               HttpStatusCode.Accepted,
                               new StartFunctionAttemptResponse(
                                   attemptId,
                                   Now,
                                   false,
                                   PoleFunctionProtocol.AttemptStatusPath(attemptId)),
                               null);
            return Task.FromResult(response);
        }

        public Task<FunctionAgentProtocolResponse<FunctionAttemptStatusResponse>> ObserveAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            string attemptId,
            int waitUpToMilliseconds,
            CancellationToken cancellationToken)
        {
            ObservedEndpoints.Add(endpoint);
            var response = ObserveHandler?.Invoke(endpoint, attemptId) ??
                           new FunctionAgentProtocolResponse<FunctionAttemptStatusResponse>(
                               HttpStatusCode.OK,
                               new FunctionAttemptStatusResponse(
                                   ObservedAgentInstanceId ?? endpoint.TargetUid!,
                                   Now,
                                   new CompletedFunctionAttemptStatus(
                                       attemptId,
                                       Now,
                                       Now,
                                       FunctionAttemptOutcome.Succeeded,
                                       CompletionResult,
                                       null)),
                               null);
            return Task.FromResult(response);
        }

        public static FunctionAgentProtocolResponse<ReserveFunctionAttemptResponse> SuccessfulReservation(
            ReserveFunctionAttemptRequest request) => new(
            HttpStatusCode.OK,
            new ReserveFunctionAttemptResponse(
                request.Attempt.AttemptId,
                "reservation-token",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddSeconds(5),
                false),
            null);
    }
}
