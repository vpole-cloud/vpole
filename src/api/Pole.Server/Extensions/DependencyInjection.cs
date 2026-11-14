using Microsoft.AspNetCore.Authentication.JwtBearer;
using Pole.ASP.Helpers;

namespace Pole.Server.Extensions;

public static class DependencyInjection
{
    extension(WebApplicationBuilder builder)
    {
        public WebApplicationBuilder AddSecurity()
        {
            if (builder.Environment.IsDevelopment())
                builder.Services.AddAntiforgery();

            var configuredValidIssuer = builder.Configuration["Authentication:ValidIssuer"];
            builder.Services
                .AddAuthentication(options =>
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
                builder.Services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options =>
                    {
                        options.TokenValidationParameters.ValidIssuer = configuredValidIssuer;
                        options.TokenValidationParameters.ValidIssuers = [configuredValidIssuer];
                    });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy(
                    Policy.Admin,
                    policy => policy.RequireAuthenticatedUser().RequireClaim(Claims.Roles, Roles.Admin));
            });

            return builder;
        }
    }
}
