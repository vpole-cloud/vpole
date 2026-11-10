using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
        services.TryAddEnumerable(
            ServiceDescriptor
                .Singleton<IValidateOptions<PrivateAccessBrokerOptions>, PrivateAccessBrokerOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PrivateAccessGatewayRegistry>();
        services.AddSingleton<PrivateAccessGatewayRendezvous>();
        services.AddSingleton<PrivateAccessSessionCapacity>();
        services.AddSingleton<PrivateAccessDrainState>();
        services.AddScoped<PrivateAccessBrokerRouting>();
        if (configuration.GetValue<bool>($"{PrivateAccessBrokerOptions.Section}:Enabled"))
            services.AddScoped<PrivateAccessPeerRelay>();
        services.AddScoped<PrivateAccessGatewayResources>();
        services.AddScoped<EnsurePrivateAccessGateway>(provider =>
            provider.GetRequiredService<PrivateAccessGatewayResources>().EnsureAsync);
        services.AddScoped<DeletePrivateAccessGatewayEndpoint>(provider =>
            provider.GetRequiredService<PrivateAccessGatewayResources>().DeleteEndpointAsync);
        services.AddScoped<DeletePrivateAccessGateway>(provider =>
            provider.GetRequiredService<PrivateAccessGatewayResources>().DeleteAsync);
        services.AddScoped<PrivateAccessGatewayReconciler>();
        services.AddScoped<PrivateAccessSessionRunner>();
        services.AddScoped<PrivateAccessSessionCleanup>();
        services.AddHostedService<PrivateAccessGatewayReconciliationService>();
        services.AddHostedService<PrivateAccessSessionCleanupService>();
        return services;
    }

    public static WebApplicationBuilder ConfigurePrivateAccessListener(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration
            .GetSection(PrivateAccessBrokerOptions.Section)
            .Get<PrivateAccessBrokerOptions>();
        if (options?.Enabled != true)
            return builder;
        var endpointUrl = builder.Configuration["Kestrel:Endpoints:PrivateAccessGateway:Url"];
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var endpointUri) || endpointUri.Scheme != "https" ||
            endpointUri.Port != options.GatewayPort)
            throw new InvalidOperationException(
                "Configure the PrivateAccessGateway HTTPS Kestrel endpoint on PrivateAccess:GatewayPort.");
        var internalUrl = builder.Configuration["Kestrel:Endpoints:PrivateAccessInternal:Url"];
        if (!Uri.TryCreate(internalUrl, UriKind.Absolute, out var internalUri) || internalUri.Scheme != "https" ||
            internalUri.Port != options.InternalPort)
            throw new InvalidOperationException(
                "Configure the PrivateAccessInternal HTTPS Kestrel endpoint on PrivateAccess:InternalPort.");
        var peer = new PrivateAccessInternalIdentity(
            options.InternalCertificatePath,
            options.InternalCertificatePassword,
            options.InternalCertificateSha256Pin);
        builder.Services.AddSingleton(peer);
        builder.WebHost.ConfigureKestrel((context, server) => server
            .Configure(context.Configuration.GetSection("Kestrel"))
            .Endpoint(
                "PrivateAccessGateway",
                endpoint =>
                {
                    endpoint.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    // TLS establishes possession. The route admits only a fresh database-backed
                    // exact certificate pin, rather than trusting arbitrary self-signed chains.
                    endpoint.HttpsOptions.ClientCertificateValidation = (
                            certificate, _,
                            _) => certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
                        certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow;
                })
            .Endpoint(
                "PrivateAccessInternal",
                endpoint =>
                {
                    endpoint.HttpsOptions.ServerCertificate = peer.Certificate;
                    endpoint.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    endpoint.HttpsOptions.ClientCertificateValidation = (
                        certificate, _,
                        _) => peer.Matches(certificate);
                }));
        return builder;
    }

    public static WebApplication UsePrivateAccessRuntime(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<PrivateAccessBrokerOptions>>().Value;
        if (options.Enabled)
        {
            var drain = app.Services.GetRequiredService<PrivateAccessDrainState>();
            app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(drain.Begin);
        }

        app.Use(async (context, next) =>
        {
            if (options.Enabled && context.Connection.LocalPort == options.GatewayPort &&
                context.Request.Path != "/private-access/gateways" &&
                context.Request.Path != "/private-access/gateway-streams")
            {
                context.Response.StatusCode = 404;
                return;
            }

            if (options.Enabled && context.Connection.LocalPort == options.InternalPort &&
                !context.Request.Path.StartsWithSegments("/_private-access/relay"))
            {
                context.Response.StatusCode = 404;
                return;
            }

            if (options.Enabled && context.Request.Path.StartsWithSegments("/_private-access/relay") &&
                context.Connection.LocalPort != options.InternalPort)
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

public sealed class PrivateAccessSessionCapacity(
    IOptions<PrivateAccessBrokerOptions> options
) : IDisposable
{
    public SemaphoreSlim Sessions { get; } = new(options.Value.MaximumSessions, options.Value.MaximumSessions);

    public SemaphoreSlim Gateways { get; } = new(
        options.Value.MaximumGatewayControls,
        options.Value.MaximumGatewayControls);

    public void Dispose()
    {
        Sessions.Dispose();
        Gateways.Dispose();
    }
}
