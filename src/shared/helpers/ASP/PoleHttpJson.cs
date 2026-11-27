// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Pole.ASP.Helpers;

/// <summary>The JSON wire contract for Pole HTTP APIs.</summary>
public static class PoleHttpJson
{
    public static IServiceCollection AddPoleHttpJson(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options => Configure(options.SerializerOptions));
        return services;
    }

    public static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }

    private static void Configure(JsonSerializerOptions options)
    {
        // Register only on the HTTP options: persistence, events and external protocols own
        // their serialization contracts independently. Preserve the declared enum names.
        if (!options.Converters.Any(converter => converter is HttpEnumConverter))
            options.Converters.Insert(0, new HttpEnumConverter());
    }

    private sealed class HttpEnumConverter() : JsonStringEnumConverter(allowIntegerValues: false);
}
