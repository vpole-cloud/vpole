using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.MongoDB.Configuration;
using Orleans.Providers.MongoDB.StorageProviders.Serializers;
using Orleans.Providers.MongoDB.Utils;
using Pole.Features.Functions.Runtime.Coordination;

namespace Pole.Server.Extensions;

public static class FunctionOrleansHosting
{
    public static IHostApplicationBuilder AddFunctionOrleansHosting(this IHostApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(FunctionOrleansOptions.SectionName);
        var options = section.Get<FunctionOrleansOptions>() ?? new FunctionOrleansOptions();

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<FunctionOrleansOptions>, FunctionOrleansOptionsValidator>());
        builder.Services.AddOptions<FunctionOrleansOptions>().Bind(section).ValidateDataAnnotations().ValidateOnStart();

        // The Aspire resource already owns the MongoClient and its connection lifecycle.
        // The Orleans provider factory only adapts that same client to its named-client contract.
        builder.Services.TryAddSingleton<IMongoClientFactory, DefaultMongoClientFactory>();
        builder.Services.AddKeyedSingleton<IGrainStateSerializer, BinaryGrainStateSerializer>(
            FunctionCoordinationStorage.ProviderName);

        builder.UseOrleans(siloBuilder =>
        {
            if (options.UseKubernetesHosting)
            {
                siloBuilder.UseKubernetesHosting(optionsBuilder =>
                    optionsBuilder.Configure(kubernetesOptions => kubernetesOptions.DeleteDefunctSiloPods = false));
                siloBuilder.Configure<EndpointOptions>(endpointOptions =>
                {
                    endpointOptions.SiloPort = options.SiloPort;
                    endpointOptions.GatewayPort = options.GatewayPort;
                });
            }
            else
            {
                var advertisedAddress = IPAddress.TryParse(options.AdvertisedAddress, out var parsedAddress)
                    ? parsedAddress
                    : IPAddress.Loopback;
                siloBuilder.ConfigureEndpoints(
                    advertisedAddress,
                    options.SiloPort,
                    options.GatewayPort,
                    listenOnAnyHostAddress: true);
            }

            // Apply these after Kubernetes hosting so Functions:Orleans remains the stable
            // cluster identity even when ORLEANS_* environment variables are present.
            siloBuilder.Configure<ClusterOptions>(clusterOptions =>
            {
                clusterOptions.ServiceId = options.ServiceId;
                clusterOptions.ClusterId = options.ClusterId;
            });
            siloBuilder.Configure<SiloMessagingOptions>(messagingOptions =>
                messagingOptions.ResponseTimeout = TimeSpan.FromSeconds(options.ResponseTimeoutSeconds));

            siloBuilder.UseMongoDBClustering(mongoOptions =>
            {
                mongoOptions.DatabaseName = options.DatabaseName;
                mongoOptions.CollectionPrefix = options.CollectionPrefix;
                mongoOptions.Strategy = MongoDBMembershipStrategy.Multiple;
            });
            siloBuilder.AddMongoDBGrainStorage(
                FunctionCoordinationStorage.ProviderName,
                mongoOptions =>
                {
                    mongoOptions.DatabaseName = options.DatabaseName;
                    mongoOptions.CollectionPrefix = options.CollectionPrefix;
                });

#pragma warning disable ORLEANSEXP003 // Selected explicitly for multi-silo grain ownership.
            siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
        });

        return builder;
    }
}

public sealed class FunctionOrleansOptions
{
    public const string SectionName = "Functions:Orleans";

    [Required]
    [RegularExpression(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        ErrorMessage = "ServiceId must contain only letters, digits, dots, underscores, or hyphens.")]
    public string ServiceId { get; set; } = "pole-functions";

    [Required]
    [RegularExpression(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        ErrorMessage = "ClusterId must contain only letters, digits, dots, underscores, or hyphens.")]
    public string ClusterId { get; set; } = "pole-functions-local";

    [Required]
    [RegularExpression(
        "^[A-Za-z0-9][A-Za-z0-9_-]{0,62}$",
        ErrorMessage = "DatabaseName must contain only letters, digits, underscores, or hyphens.")]
    public string DatabaseName { get; set; } = "pole-orleans";

    [Required]
    [RegularExpression(
        "^[A-Za-z0-9][A-Za-z0-9_-]{0,62}$",
        ErrorMessage = "CollectionPrefix must contain only letters, digits, underscores, or hyphens.")]
    public string CollectionPrefix { get; set; } = "FunctionsOrleans_";

    public bool UseKubernetesHosting { get; set; }

    [Required] public string AdvertisedAddress { get; set; } = IPAddress.Loopback.ToString();

    [Range(1, 65_535)] public int SiloPort { get; set; } = 11_111;

    [Range(0, 65_535)] public int GatewayPort { get; set; }

    [Range(1, 60)] public int ResponseTimeoutSeconds { get; set; } = 10;
}

public sealed class FunctionOrleansOptionsValidator : IValidateOptions<FunctionOrleansOptions>
{
    public ValidateOptionsResult Validate(string? name, FunctionOrleansOptions options)
    {
        var failures = new List<string>();
        if (!options.UseKubernetesHosting && !IPAddress.TryParse(options.AdvertisedAddress, out _))
            failures.Add(
                $"{nameof(options.AdvertisedAddress)} must be a valid IP address when Kubernetes hosting is disabled.");
        if (options.GatewayPort != 0 && options.GatewayPort == options.SiloPort)
            failures.Add($"{nameof(options.GatewayPort)} and {nameof(options.SiloPort)} must be distinct.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
