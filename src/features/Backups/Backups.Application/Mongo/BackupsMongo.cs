// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Backups.Domain;

namespace Pole.Features.Backups.Application.Mongo;

internal static class BackupsCollections
{
    internal const string Destinations = "BackupDestinations";
    internal const string Policies = "BackupPolicies";
    internal const string Archives = "BackupArchives";
    internal const string Operations = "BackupOperations";
}

internal static class BackupsIndexes
{
    internal const string DestinationName = "UX_BackupDestinations_Tenant_Name";
    internal const string PolicyName = "UX_BackupPolicies_Tenant_Name";
    internal const string PolicySource = "UX_BackupPolicies_Tenant_Source";
    internal const string OperationKey = "UX_BackupOperations_Tenant_Key";
}

internal static class BackupsMongoRegistration
{
    internal static void AddBackupsMongo(this IServiceCollection services)
    {
        Add<BackupDestination>(services, BackupsCollections.Destinations);
        Add<BackupPolicy>(services, BackupsCollections.Policies);
        Add<BackupArchive>(services, BackupsCollections.Archives);
        Add<BackupOperation>(services, BackupsCollections.Operations);
    }

    private static void Add<T>(IServiceCollection services, string collection) => services.AddSingleton(provider =>
        provider.GetRequiredService<IMongoDatabase>().GetCollection<T>(collection));
}
