using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Pole.Features.PrivateAccess.Runtime;

public static class PrivateAccessRuntime
{
    public static IServiceCollection AddPrivateAccessRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<PrivateAccessBrokerOptions>()
            .Bind(configuration.GetSection(PrivateAccessBrokerOptions.Section))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PrivateAccessBrokerOptions>,
            PrivateAccessBrokerOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PrivateAccessConnectorRegistry>();
        services.AddSingleton<PrivateAccessWorkerRendezvous>();
        services.AddSingleton<PrivateAccessSessionCapacity>();
        services.AddScoped<PrivateAccessWorkerResources>();
        services.AddScoped<PrivateAccessSessionRunner>();
        services.AddScoped<PrivateAccessSessionCleanup>();
        services.AddScoped<DeletePrivateAccessWorker>(provider =>
            provider.GetRequiredService<PrivateAccessWorkerResources>().DeleteAsync);
        services.AddHostedService<PrivateAccessSessionCleanupService>();
        return services;
    }

    public static WebApplicationBuilder ConfigurePrivateAccessListener(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(PrivateAccessBrokerOptions.Section)
            .Get<PrivateAccessBrokerOptions>();
        if (options?.Enabled != true)
            return builder;
        var endpointUrl = builder.Configuration["Kestrel:Endpoints:PrivateAccessConnector:Url"];
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme != "https" || endpointUri.Port != options.ConnectorPort)
            throw new InvalidOperationException("Configure the PrivateAccessConnector HTTPS Kestrel endpoint on PrivateAccess:ConnectorPort.");
        builder.WebHost.ConfigureKestrel((context, server) =>
            server.Configure(context.Configuration.GetSection("Kestrel"))
                .Endpoint("PrivateAccessConnector", endpoint =>
                {
                    endpoint.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    // TLS establishes possession. The route admits only a fresh database-backed
                    // exact certificate pin, rather than trusting arbitrary self-signed chains.
                    endpoint.HttpsOptions.ClientCertificateValidation = (certificate, _, _) =>
                        certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
                        certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow;
                }));
        return builder;
    }

    public static WebApplication UsePrivateAccessRuntime(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<PrivateAccessBrokerOptions>>().Value;
        app.Use(async (context, next) =>
        {
            if (options.Enabled && context.Connection.LocalPort == options.ConnectorPort &&
                context.Request.Path != "/private-access/connectors")
            {
                context.Response.StatusCode = 404;
                return;
            }
            await next(context);
        });
        app.UseWebSockets();
        return app;
    }
}

public sealed class PrivateAccessSessionCapacity(IOptions<PrivateAccessBrokerOptions> options) : IDisposable
{
    public SemaphoreSlim Sessions { get; } = new(options.Value.MaximumSessions, options.Value.MaximumSessions);
    public SemaphoreSlim Connectors { get; } = new(64, 64);
    public void Dispose()
    {
        Sessions.Dispose();
        Connectors.Dispose();
    }
}
