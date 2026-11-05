using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitProviders.Application.Endpoints.Callbacks;
using Pole.Features.GitProviders.Application.Webhooks;
using Pole.Features.GitProviders.Domain;
using Pole.Features.GitProviders.Events;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Outbox.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitProviders;

public sealed class GitHubWebhookTests(MongoReplicaSetFixture fixture) : ApiIntegrationTestBase(fixture)
{
    private const string Path = "/api/v1/integrations/github/webhook";
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private IMongoCollection<GitHubDeliveryReceipt> Receipts => Fixture.Database
        .GetCollection<GitHubDeliveryReceipt>("GitHubDeliveryReceipts");

    private IMongoCollection<OutboxMessage> Outbox => Fixture.Database.GetCollection<OutboxMessage>("OutboxMessages");

    [Fact]
    public async Task ConcurrentSignedDeliveryProducesOneEventWithStoredTenantAndOnlySafeMetadata()
    {
        var connection = await Connection();
        var delivery = Guid.NewGuid().ToString("D");
        var payload = Push("refs/heads/feature/api");
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Send(payload, delivery)));
        Assert.All(responses, item => Assert.Equal(HttpStatusCode.Accepted, item.StatusCode));
        var receipt = await Receipts.Find(Builders<GitHubDeliveryReceipt>.Filter.Empty).SingleAsync();
        Assert.Equal(receipt, BsonSerializer.Deserialize<GitHubDeliveryReceipt>(receipt.ToBson()));
        Assert.DoesNotContain("payload-secret-marker", receipt.ToJson());
        var message = await Outbox.Find(Builders<OutboxMessage>.Filter.Empty).SingleAsync();
        var value = JsonSerializer.Deserialize<GitRepositoryPushReceived>(message.PayloadJson,
            EventExtensions.SerializerOptions)!;
        Assert.Equal(TestData.TenantId, value.EventCore.TenantId);
        Assert.Equal(connection.Id, value.ConnectionId);
        Assert.Equal("feature/api", value.Branch);
        Assert.Equal("77", value.ExternalRepositoryId);
        Assert.DoesNotContain("payload-secret-marker", message.PayloadJson);
        Assert.DoesNotContain("untrusted-tenant", message.PayloadJson);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(Push("refs/heads/other"), delivery)).StatusCode);
        Assert.Equal(1, await Outbox.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("tampered")]
    public async Task InvalidSignaturesCannotCreateReceiptOrOutbox(string mode)
    {
        await Connection();
        var body = Push();
        var signature = mode == "missing" ? "" : mode == "invalid" ? "sha256=" + new string('0', 64) : Sign(body);
        if (mode == "tampered")
            body += " ";
        using var response = await Send(body, Guid.NewGuid().ToString(), signature: signature);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await Receipts.CountDocumentsAsync(Builders<GitHubDeliveryReceipt>.Filter.Empty));
        Assert.Equal(0, await Outbox.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
    }

    [Theory]
    [InlineData("ping", "{}")] // Signed non-push deliveries are acknowledged without a GitOps signal.
    [InlineData("pull_request", "{}")]
    [InlineData("push", "{\"ref\":\"refs/tags/v1\"}")]
    [InlineData("push",
        "{\"ref\":\"refs/heads/main\",\"after\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"repository\":{\"id\":77},\"installation\":{\"id\":999}}")]
    public async Task IrrelevantDeliveriesDoNotEnqueueSources(string type, string body)
    {
        await Connection();
        Assert.Equal(HttpStatusCode.Accepted, (await Send(body, Guid.NewGuid().ToString(), type)).StatusCode);
        Assert.Equal(1, await Receipts.CountDocumentsAsync(Builders<GitHubDeliveryReceipt>.Filter.Empty));
        Assert.Equal(0, await Outbox.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
    }

    [Fact]
    public async Task BranchDeletionStillSchedulesAResolveAndBodyLimitIsEnforced()
    {
        await Connection();
        var body = Push().Replace(Sha, new string('0', 40));
        Assert.Equal(HttpStatusCode.Accepted, (await Send(body, Guid.NewGuid().ToString())).StatusCode);
        Assert.Equal(1, await Outbox.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = new ByteArrayContent(new byte[25 * 1024 * 1024 + 1])
        };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task MalformedPushAndDeliveryIdAreRejectedAfterSignatureVerification()
    {
        foreach (var body in new[] { "{", "[]", "{\"ref\":\"refs/heads/main\"}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Send(body, Guid.NewGuid().ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Push(), "not-a-delivery-id")).StatusCode);
        Assert.Equal(0, await Receipts.CountDocumentsAsync(Builders<GitHubDeliveryReceipt>.Filter.Empty));
    }

    [Fact]
    public async Task ReceiptAndEventRollBackTogetherWhenOutboxWriteFails()
    {
        await Connection();
        var store = ApiServices.GetRequiredService<StoreOutboxWrites>();
        StoreOutboxWrites failing = async (session, writes, ct) =>
        {
            await store(session, writes, ct);
            throw new IOException("Injected after outbox write");
        };
        var persistence = ActivatorUtilities.CreateInstance<GitHubWebhookPersistence>(ApiServices, failing);
        await Assert.ThrowsAsync<IOException>(() => persistence.StoreAsync(
            new(Guid.NewGuid().ToString(), new string('a', 64), "push", DateTime.UtcNow),
            new("42", "77", "main", Sha),
            default));
        Assert.Equal(0, await Receipts.CountDocumentsAsync(Builders<GitHubDeliveryReceipt>.Filter.Empty));
        Assert.Equal(0, await Outbox.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
    }

    [Fact]
    public async Task StreamingBodyWithoutContentLengthStillHasTheSameLimit()
    {
        using var stream = new StreamingPayload(new byte[25 * 1024 * 1024 + 1]);
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = new StreamContent(stream)
        };
        Assert.Null(request.Content.Headers.ContentLength);
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, await Receipts.CountDocumentsAsync(Builders<GitHubDeliveryReceipt>.Filter.Empty));
    }

    private sealed class StreamingPayload(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }

    private async Task<GitProviderConnection> Connection()
    {
        var connection = GitProviderConnectionFactory.Create(TestData.TenantId,
            "github",
            GitProviderKind.GitHub,
            "42",
            "1",
            "test-owner",
            GitProviderAccountType.Organization,
            GitProviderRepositorySelection.All,
            TestData.UserId,
            GitProviderConnectionFactory.DefaultOptions);
        await Fixture.Database.GetCollection<GitProviderConnection>("GitProviderConnections")
            .InsertOneAsync(connection);
        return connection;
    }

    private static string Push(string branch = "refs/heads/main") => JsonSerializer.Serialize(new
    {
        @ref = branch,
        after = Sha,
        installation = new { id = 42 },
        repository = new { id = 77 },
        tenantId = "untrusted-tenant",
        commits = new[] { new { message = "payload-secret-marker" } }
    });

    private static string Sign(string body) => "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(TestApiApplication.GitHubWebhookSecret),
        Encoding.UTF8.GetBytes(body)));

    private Task<HttpResponseMessage> Send(string body, string delivery, string type = "push", string? signature = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent(body) };
        request.Headers.Add("X-GitHub-Delivery", delivery);
        request.Headers.Add("X-GitHub-Event", type);
        request.Headers.Add("X-Hub-Signature-256", signature ?? Sign(body));
        return Client.SendAsync(request);
    }
}
