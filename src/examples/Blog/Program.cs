using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Blog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pole.ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "http://pole-server";

builder.Services.AddHttpClient<BlogSeedClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl.TrimEnd('/'));
});

using var host = builder.Build();
using var scope = host.Services.CreateScope();

var seeder = scope.ServiceProvider.GetRequiredService<BlogSeedClient>();
await seeder.RunAsync();

namespace Blog
{
    internal sealed class BlogSeedClient(HttpClient httpClient)
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public async Task RunAsync()
        {
            Console.WriteLine($"Using API base URL: {httpClient.BaseAddress}");

            var resourceGroup = await EnsureResourceGroupAsync(
                name: "blog-prod",
                description: "Production workloads for the blog platform.",
                tags: ["blog", "prod", "content"]);

            await EnsureWebServiceAsync(resourceGroup.Id, "blog-microservice", "blog.example.local");
            await EnsureWebServiceAsync(resourceGroup.Id, "admin-panel", "admin.example.local");
            await EnsureWebServiceAsync(resourceGroup.Id, "public-frontend", "www.example.local");
            await EnsureWebServiceAsync(resourceGroup.Id, "image-proxy", "images.example.local");
            await EnsurePostgresClusterAsync(resourceGroup.Id, "blog-postgres", "blog_admin", "blog-admin-secret");
            await EnsurePostgresClusterAsync(resourceGroup.Id, "analytics-postgres", "analytics", "analytics-secret");

            Console.WriteLine("Seed data is ready.");
        }

        private async Task<ResourceGroupResponse> EnsureResourceGroupAsync(
            string name,
            string description,
            IReadOnlyCollection<string> tags)
        {
            var existing = await FindResourceGroupByNameAsync(name);
            if (existing is not null)
            {
                Console.WriteLine($"Resource group already exists: {existing.Name} ({existing.Id})");
                return existing;
            }

            var response = await httpClient.PostAsJsonAsync(
                "/api/v1/resource-groups/",
                new
                {
                    Name = name,
                    Description = description,
                    Tags = tags
                });

            response.EnsureSuccessStatusCode();
            var created = await ReadJsonAsync<ResourceGroupResponse>(response);
            Console.WriteLine($"Created resource group: {created.Name} ({created.Id})");
            return created;
        }

        private async Task EnsureWebServiceAsync(string resourceGroupId, string name, string domainName)
        {
            var existing = await FindWebServiceByNameAsync(name);
            if (existing is not null)
            {
                Console.WriteLine($"Web service already exists: {existing.Name} ({existing.Id})");
                return;
            }

            var response = await httpClient.PostAsJsonAsync(
                "/api/v1/web-services/",
                new
                {
                    Name = name,
                    ResourceGroupId = resourceGroupId,
                    Spec = new
                    {
                        Image = new
                        {
                            Server = "docker.io",
                            Name = "nginx",
                            Tag = "1.27"
                        },
                        Replicas = 2,
                        Resources = new
                        {
                            Requests = new { CpuCores = 1, MemoryMi = 256 },
                            Limits = new { CpuCores = 2, MemoryMi = 512 }
                        },
                        Config = new
                        {
                            EnvironmentVariables = new Dictionary<string, string>
                            {
                                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                                ["SERVICE_NAME"] = name
                            },
                            PortMappings = new[]
                            {
                                new
                                {
                                    ServicePort = 80,
                                    TargetPort = "8080",
                                    Protocol = "TCP",
                                    Name = "http"
                                }
                            }
                        },
                        Autoscaling = (object?)null,
                        Probes = new
                        {
                            Startup = CreateProbe(),
                            Liveness = CreateProbe(),
                            Readiness = CreateProbe()
                        }
                    },
                    DomainName = domainName
                });

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                Console.WriteLine($"Skipped web service because domain is already used: {domainName}");
                return;
            }

            response.EnsureSuccessStatusCode();
            var created = await ReadJsonAsync<WebServiceCreateResponse>(response);
            Console.WriteLine($"Created web service: {name} ({created.WebServiceId})");
        }

        private async Task EnsurePostgresClusterAsync(string resourceGroupId, string name, string username, string password)
        {
            var existing = await FindPostgresClusterByNameAsync(name);
            if (existing is not null)
            {
                Console.WriteLine($"Postgres cluster already exists: {existing.Name} ({existing.Id})");
                return;
            }

            var response = await httpClient.PostAsJsonAsync(
                "/api/v1/postgres/",
                new
                {
                    Name = name,
                    GroupId = resourceGroupId,
                    Version = 17,
                    Instances = 1,
                    Storage = 10,
                    Username = username,
                    Password = password
                });

            response.EnsureSuccessStatusCode();
            var created = await ReadJsonAsync<PostgresClusterResponse>(response);
            Console.WriteLine($"Created postgres cluster: {created.Name} ({created.Id})");
        }

        private async Task<ResourceGroupResponse?> FindResourceGroupByNameAsync(string name)
        {
            var response = await httpClient.GetAsync($"/api/v1/resource-groups/?search={Uri.EscapeDataString(name)}&pageNumber=1&pageSize=50");
            response.EnsureSuccessStatusCode();

            var page = await ReadJsonAsync<PaginatedResponse<ResourceGroupResponse>>(response);
            return page.Items.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<WebServiceResponse?> FindWebServiceByNameAsync(string name)
        {
            var response = await httpClient.GetAsync($"/api/v1/web-services/?search={Uri.EscapeDataString(name)}&pageNumber=1&pageSize=50");
            response.EnsureSuccessStatusCode();

            var page = await ReadJsonAsync<PaginatedResponse<WebServiceResponse>>(response);
            return page.Items.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<PostgresClusterResponse?> FindPostgresClusterByNameAsync(string name)
        {
            var response = await httpClient.GetAsync("/api/v1/postgres/?pageNumber=1&pageSize=100");
            response.EnsureSuccessStatusCode();

            var page = await ReadJsonAsync<PaginatedResponse<PostgresClusterResponse>>(response);
            return page.Items.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        {
            var payload = await response.Content.ReadFromJsonAsync<T>(JsonOptions);
            return payload ?? throw new InvalidOperationException($"Expected JSON payload for {typeof(T).Name}.");
        }

        private static object CreateProbe() => new
        {
            Path = "/health",
            Port = "8080",
            InitialDelaySeconds = 5,
            PeriodSeconds = 10,
            TimeoutSeconds = 5,
            FailureThreshold = 3,
            SuccessThreshold = 1
        };

        private sealed record ResourceGroupResponse(string Id, string Name, string Description, IReadOnlyCollection<string> Tags);

        private sealed record WebServiceCreateResponse(string WebServiceId);

        private sealed record WebServiceResponse(string Id, string Name, string ResourceGroupId);

        private sealed record PostgresClusterResponse(string Id, string Name, string GroupId);

        private sealed record PaginatedResponse<T>(IReadOnlyCollection<T> Items, long TotalCount, int PageNumber, int PageSize);
    }
}
