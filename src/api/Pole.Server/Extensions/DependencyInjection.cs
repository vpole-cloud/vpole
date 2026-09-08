using System.Reflection;
using FluentValidation;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application;
using Pole.Mediator;
using Pole.Observability;

namespace Pole.Server.Extensions;

public static class DependencyInjection
{
    extension(WebApplicationBuilder builder)
    {
        public  WebApplicationBuilder AddSecurity()
        {
            var configuredValidIssuer = builder.Configuration["Authentication:ValidIssuer"];
            builder.Services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;

                })
                .AddKeycloakJwtBearer(
                    serviceName: "pole-keycloak",
                    realm: "pole.control-plane",
                    options =>
                    {
                        options.MapInboundClaims = false;
                        options.Audience = "pole.control-plane.api";
                        if (builder.Environment.IsDevelopment())
                            options.RequireHttpsMetadata = false;
                    });
            if (!string.IsNullOrWhiteSpace(configuredValidIssuer))
            {
                builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.TokenValidationParameters.ValidIssuer = configuredValidIssuer;
                    options.TokenValidationParameters.ValidIssuers = [configuredValidIssuer];
                });
            }
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy(Policy.Admin, policy =>
                    policy.RequireClaim(Claims.Roles, Roles.Admin));
            });

            return builder;
        }
    }

}
