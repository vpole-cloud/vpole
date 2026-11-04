using Pole.Features.GitProviders.Application.Endpoints.Callbacks;
using Pole.Features.GitProviders.Application.Persistence.Webhooks;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;
using Pole.Features.GitProviders.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitProviders.Application.Snapshots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using Pole.Features.GitProviders.Application.Analysis;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.GitHub;
using Pole.Features.GitProviders.Application.Persistence;
using Pole.Features.GitProviders.Application.Persistence.Connections;
using Pole.Features.GitProviders.Application.Persistence.Repositories;
using Pole.Features.GitProviders.Application.Persistence.Setup;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.Realtime.Contracts;

namespace Pole.Features.GitProviders.Application;

public static class GitProviderApplicationRegistration
{
    public static IServiceCollection AddGitProvidersFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(new RealtimeTopic("git", RealtimeScope.Tenant));
        services.Configure<GitHubAppOptions>(
            configuration.GetSection(GitHubAppOptions.SectionName));
        services.TryAddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<GitHubAppOptions>>().Value);
        services.AddOptions<RepositoryArchiveOptions>()
            .Bind(configuration.GetSection(RepositoryArchiveOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<RepositoryArchiveOptions>>().Value);
        services.TryAddScoped<RepositoryManifestArchiveReader>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IRepositorySnapshotProvider, GitHubRepositorySnapshotProvider>());
        services.AddOptions<GitHubWebhookOptions>()
            .Bind(configuration.GetSection(GitHubWebhookOptions.SectionName))
            .Validate(value => !configuration.GetValue<bool>("GitHubApp:Enabled") ||
                               !string.IsNullOrWhiteSpace(value.Secret),
                "GitHubWebhook:Secret is required when GitHub is enabled.")
            .ValidateOnStart();
        services.AddScoped<GitHubWebhookPersistence>();
        services.AddScoped<ReceiveGitHubWebhookHandler>();
        services.AddMemoryCache();
        services.AddDataProtection().SetApplicationName("Pole");
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<GitHubSetupTokenService>();
        services.TryAddScoped<GitHubSetupPersistence>();
        services.TryAddScoped<GitHubSetupService>();
        services.TryAddScoped<RepositorySourceAnalyzer>();
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<SourceRepository>("SourceRepositories"));
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<GitProviderConnection>("GitProviderConnections"));
        services.TryAddScoped<MongoSourceRepositoryReferences>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDeclarativeResourceProvider,
            DeclarativeSourceRepositoryProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDeclarativeResourceLifecycleProvider,
            DeclarativeSourceRepositoryProvider>());
        services.TryAddScoped<FindNamedSourceRepository>(provider =>
            provider.GetRequiredService<MongoSourceRepositoryReferences>().FindNamedAsync);
        services.TryAddScoped<LoadSourceRepositoryReference>(provider =>
            provider.GetRequiredService<MongoSourceRepositoryReferences>().LoadAsync);
        services.TryAddScoped<FenceSourceRepository>(provider =>
            provider.GetRequiredService<MongoSourceRepositoryReferences>().FenceAsync);
        services.AddCapability<GitHubResolveProviderRepository, ResolveProviderRepository>(operation =>
            operation.InvokeAsync);
        services.AddCapability<SourceRepositoryCommitResolver, ResolveSourceRepositoryCommit>(operation =>
            operation.InvokeAsync);
        services.AddCapability<SourceRepositoryBuildAccessChecker, CheckSourceRepositoryBuildAccess>(operation =>
            operation.InvokeAsync);
        services.TryAddScoped<SourceRepositoryBuildFinder>();
        services.TryAddScoped<FindSourceRepositoryBuild>(provider =>
            provider.GetRequiredService<SourceRepositoryBuildFinder>().InvokeAsync);
        services.TryAddScoped<SourceRepositoryArchiveDownloader>();
        services.TryAddScoped<DownloadSourceRepositoryArchive>(provider =>
            provider.GetRequiredService<SourceRepositoryArchiveDownloader>().InvokeAsync);

        services.AddCapability<GitHubAppJwtFactory, CreateGitHubAppJwt>(factory => factory.Invoke);
        services.AddCapability<MongoCreateGitHubSetupAttempt, CreateGitHubSetupAttempt>(persistence =>
            persistence.InvokeAsync);
        services.AddCapability<MongoFindNamedGitProviderConnection, FindNamedGitProviderConnection>(persistence =>
            persistence.InvokeAsync);
        services.AddCapability<MongoLoadGitProviderConnection, LoadGitProviderConnection>(persistence =>
            persistence.InvokeAsync);
        services.AddCapability<MongoLoadGitProviderConnections, LoadGitProviderConnections>(persistence =>
            persistence.InvokeAsync);
        services.AddCapability<MongoDeleteGitProviderConnection, DeleteGitProviderConnection>(persistence =>
            persistence.InvokeAsync);
        services.AddCapability<MongoLoadSourceRepository, LoadSourceRepository>(persistence => persistence.InvokeAsync);
        services.AddCapability<MongoLoadSourceRepositories, LoadSourceRepositories>(persistence =>
            persistence.InvokeAsync);
        services.AddCapability<MongoPersistLinkedSourceRepository, PersistLinkedSourceRepository>(persistence =>
            persistence.InvokeAsync);
        services.AddCapability<MongoDeleteSourceRepository, DeleteSourceRepository>(persistence =>
            persistence.InvokeAsync);
        services.AddCapability<GitHubGetProviderRepositories, GetProviderRepositories>(operation =>
            operation.InvokeAsync);
        services.AddCapability<GitHubGetProviderBranches, GetProviderBranches>(operation => operation.InvokeAsync);
        services.AddCapability<GitHubGetProviderRepositorySnapshot, GetProviderRepositorySnapshot>(operation =>
            operation.InvokeAsync);
        services.AddCapability<GitHubDownloadProviderRepositoryArchive, DownloadProviderRepositoryArchive>(operation =>
            operation.InvokeAsync);
        services.AddCapability<GitProviderRepositoryAnalyzer, AnalyzeProviderRepository>(operation =>
            operation.InvokeAsync);
        services.AddHttpClient<IGitHubAppClient, GitHubAppClient>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Pole/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }

    private static void AddCapability<TImplementation, TDelegate>(
        this IServiceCollection services,
        Func<TImplementation, TDelegate> bind)
        where TImplementation : class
        where TDelegate : Delegate
    {
        services.TryAddScoped<TImplementation>();
        services.TryAddScoped(serviceProvider =>
            bind(serviceProvider.GetRequiredService<TImplementation>()));
    }
}
