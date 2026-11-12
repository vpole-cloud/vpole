using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Primitives;

namespace Pole.Features.Backups.Application.Declarations;

public static class BackupDeclarationRegistration
{
    public static IServiceCollection AddBackupDeclarations(this IServiceCollection services)
    {
        services.AddScoped<DeclarativeBackupDestinationProvider>();
        services.AddScoped<DeclarativeBackupPolicyProvider>();
        services.AddScoped<IDeclarativeResourceProvider>(sp =>
            sp.GetRequiredService<DeclarativeBackupDestinationProvider>());
        services.AddScoped<IDeclarativeResourceProvider>(sp =>
            sp.GetRequiredService<DeclarativeBackupPolicyProvider>());
        services.AddScoped<IDeclarativeResourceLifecycleProvider>(sp =>
            sp.GetRequiredService<DeclarativeBackupDestinationProvider>());
        services.AddScoped<IDeclarativeResourceLifecycleProvider>(sp =>
            sp.GetRequiredService<DeclarativeBackupPolicyProvider>());
        services.AddScoped<IDeclarativeLifecycleDependencyProvider, BackupDeclarationDependencies>();
        services.AddSingleton(
            new SecretDeclarationConsumer(
                DeclarativeBackupDestinationProvider.DeclarationKind,
                BackupConfigurationWriter.DestinationType,
                SecretBindingUsage.BackupStorage));
        return services;
    }
}
