// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Net;
using System.Text;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Security;
using Pole.Functions.Protocol;
using Xunit;

namespace Pole.Features.Functions.Tests;

public sealed class FunctionAgentClientAuthenticationTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true, "agent_unauthorized")]
    [InlineData(HttpStatusCode.Unauthorized, false, "agent_unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, true, "agent_forbidden")]
    [InlineData(HttpStatusCode.Forbidden, false, "agent_forbidden")]
    public async Task Empty_authentication_rejection_preserves_status_without_expecting_protocol_json(
        HttpStatusCode status,
        bool knownLength,
        string code)
    {
        using var content = knownLength
            ? (HttpContent)new ByteArrayContent([])
            : new StreamContent(new UnknownLengthStream([]));
        using var http = new HttpClient(new ResponseHandler(status, content));

        var response = await ReadMetadataAsync(http, CancellationToken.None);

        Assert.False(response.IsSuccess);
        Assert.Null(response.Value);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, response.Error!.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Structured_authentication_error_is_preserved(HttpStatusCode status)
    {
        var error = new FunctionProtocolError("specific_rejection", "The request was rejected.");
        using var http = new HttpClient(new ResponseHandler(status, JsonContent.Create(error)));

        var response = await ReadMetadataAsync(http, CancellationToken.None);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(error, response.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, " ")]
    [InlineData(HttpStatusCode.Forbidden, "not-json")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.OK, "")]
    public async Task Malformed_or_other_empty_responses_remain_protocol_failures(HttpStatusCode status, string body)
    {
        using var http = new HttpClient(new ResponseHandler(status, new StringContent(body)));

        var error = await Assert.ThrowsAsync<FunctionAgentProtocolException>(() =>
            ReadMetadataAsync(http, CancellationToken.None));

        Assert.Equal("Function Agent returned invalid protocol JSON.", error.Message);
    }

    [Fact]
    public async Task Authentication_error_body_still_obeys_the_streaming_size_limit()
    {
        var payload = Encoding.UTF8.GetBytes(
            "{\"code\":\"rejected\",\"message\":\"" + new string('x', 4 * 1024 * 1024) + "\"}");
        using var http = new HttpClient(
            new ResponseHandler(HttpStatusCode.Forbidden, new StreamContent(new UnknownLengthStream(payload))));

        var error = await Assert.ThrowsAsync<FunctionAgentProtocolException>(() =>
            ReadMetadataAsync(http, CancellationToken.None));

        Assert.Equal("Function Agent response exceeded the protocol size limit.", error.Message);
    }

    [Fact]
    public async Task Caller_cancellation_during_empty_authentication_body_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(
            new ResponseHandler(
                HttpStatusCode.Unauthorized,
                new StreamContent(new CancellingEmptyStream(cancellation))));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadMetadataAsync(http, cancellation.Token));
    }

    private static Task<FunctionAgentProtocolResponse<FunctionAgentMetadataResponse>> ReadMetadataAsync(
        HttpClient http,
        CancellationToken ct)
    {
        var input = new FunctionRevisionSpecInput(
            new FunctionImage("registry.example", "functions", "test", null, null),
            PoleFunctionProtocol.Version,
            new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, FunctionSecretReference>.Empty),
            new FunctionResources(new FunctionResourceValues(100, 128), new FunctionResourceValues(500, 512)),
            new FunctionScalingPolicy(0, 1, 1, 1, 30, 30),
            [
                new FunctionHandlerSpec(
                    "hello",
                    1,
                    new HttpFunctionTriggerSpec("", "hello", ["GET"], FunctionExposure.Internal)),
            ]);
        var created = FunctionAppFactory.Create(
            "tenant",
            "group",
            new FunctionPlacementIdentity("deployment", "cell", "target"),
            "function-test",
            "function-test",
            input,
            [],
            DateTimeOffset.UtcNow);
        var client = new FunctionAgentClient(http, new TokenIssuer());
        return client.GetMetadataAsync(
            new FunctionEndpoint("127.0.0.1", 8080, "pod-uid"),
            created.FunctionApp,
            created.Revision,
            ct);
    }

    private sealed class ResponseHandler(
        HttpStatusCode status,
        HttpContent content
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = content });
    }

    private sealed class UnknownLengthStream(
        byte[] bytes
    ) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class CancellingEmptyStream(
        CancellationTokenSource cancellation
    ) : MemoryStream
    {
        public override bool CanSeek => false;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromResult(0);
        }
    }

    private sealed class TokenIssuer : IFunctionAgentTokenIssuer
    {
        public string Issue(FunctionAgentTokenDescriptor descriptor) => "fixture-token";

        public FunctionAgentTrustBundle GetTrustBundle() =>
            throw new InvalidOperationException("The protocol client does not request a server trust bundle.");
    }
}
