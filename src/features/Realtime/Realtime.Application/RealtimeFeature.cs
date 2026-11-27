// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pole.ASP.Helpers;
using Pole.Features.Realtime.Application.Endpoints;
using Pole.Features.Realtime.Contracts;

namespace Pole.Features.Realtime.Application;

public static class RealtimeFeature
{
    public const string CookieScheme = "Pole.Realtime";
    public const string SessionClaim = "pole.realtime.session";
    public const string Route = "/api/v1/realtime";
    public const string CorsPolicy = "pole-realtime";

    public static IServiceCollection AddRealtimeFeature(
        this IServiceCollection services,
        IConfiguration configuration,
        bool development)
    {
        services.AddOptions<RealtimeOptions>()
            .Bind(configuration.GetSection(RealtimeOptions.Section))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<RealtimeOptions>, RealtimeOptionsValidator>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<RealtimeOptions>>().Value);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<RealtimeTopicRegistry>();
        services.TryAddSingleton<RealtimeMetrics>();
        // Disabled bootstrap still returns a browser-readable 404 with exact-origin CORS.
        services.AddCors(cors => cors.AddPolicy(
            CorsPolicy,
            policy =>
            {
                var options = configuration.GetSection(RealtimeOptions.Section).Get<RealtimeOptions>() ??
                    new RealtimeOptions();
                policy.WithOrigins(options.AllowedOrigins)
                    .AllowAnyHeader()
                    .WithMethods("GET", "POST", "DELETE")
                    .AllowCredentials();
            }));
        if (!configuration.GetValue<bool>("Realtime:Enabled"))
        {
            services.AddSingleton<PublishRealtimeChange>((_, _) => Task.CompletedTask);
            services.AddSingleton<InvalidateRealtimeTenants>((_, _) => Task.CompletedTask);
            return services;
        }

        services.AddHttpContextAccessor();
        services.AddSignalR(options => options.MaximumReceiveMessageSize = 16 * 1024);
        services.AddAuthentication()
            .AddCookie(
                CookieScheme,
                options =>
                {
                    options.Cookie.Name = development ? "PoleRealtime.Dev" : "__Secure-PoleRealtime";
                    options.Cookie.Path = Route;
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.SecurePolicy
                        = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                    options.SlidingExpiration = false;
                    options.Events.OnRedirectToLogin = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    };
                    options.Events.OnRedirectToAccessDenied = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    };
                });
        services.AddOptions<CookieAuthenticationOptions>(CookieScheme)
            .Configure<IRealtimeTicketStore>((options, tickets) => options.SessionStore = tickets);
        services.AddScoped<RealtimeAccess>();
        services.AddScoped<RealtimePublisher>();
        services.AddScoped<PublishRealtimeChange>(provider =>
            provider.GetRequiredService<RealtimePublisher>().PublishAsync);
        services.AddScoped<InvalidateRealtimeTenants>(provider =>
            provider.GetRequiredService<RealtimePublisher>().InvalidateTenantsAsync);
        return services;
    }

    public static IServiceCollection AddRealtimeTopic(
        this IServiceCollection services,
        string topic,
        RealtimeScope scope) => services.AddSingleton(new RealtimeTopic(topic, scope));

    public static IEndpointRouteBuilder MapRealtimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Route).WithoutTenantContext().RequireCors(CorsPolicy);
        group.MapCreateRealtimeSession();
        if (!endpoints.ServiceProvider.GetRequiredService<RealtimeOptions>().Enabled)
            return endpoints;
        group.MapDeleteRealtimeSession();
        group.MapHub<RealtimeHub>("/hub", options => options.CloseOnAuthenticationExpiration = true)
            .WithoutTenantContext();
        return endpoints;
    }

    public static IApplicationBuilder UseRealtimeOriginGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(Route))
            {
                var options = context.RequestServices.GetRequiredService<RealtimeOptions>();
                if (!options.Enabled)
                {
                    await next(context);
                    return;
                }

                var origin = context.Request.Headers.Origin;
                // Browser same-origin SSE/long-poll GETs can omit Origin. Negotiation and
                // cookie changes still require it; fetch metadata admits only safe GETs.
                var websocket = context.WebSockets.IsWebSocketRequest || context.Request
                    .Headers
                    .Upgrade
                    .ToString()
                    .Contains("websocket", StringComparison.OrdinalIgnoreCase);
                var sameOriginRead = HttpMethods.IsGet(context.Request.Method) && !websocket && origin.Count == 0 &&
                    context.Request.Headers["Sec-Fetch-Site"] == "same-origin";
                if (!sameOriginRead && (origin.Count != 1 ||
                        !options.AllowedOrigins.Contains(origin[0], StringComparer.Ordinal)))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }

            await next(context);
        });
}
