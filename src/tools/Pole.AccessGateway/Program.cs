// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.AccessGateway;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            lifetime.Cancel();
        };

        try
        {
            switch (args)
            {
                case ["gateway"]:
                    _ = RequiredEnvironment("POLE_ACCESS_TENANT");
                    _ = RequiredEnvironment("POLE_ACCESS_TARGET");
                    _ = RequiredEnvironment("POLE_ACCESS_GATEWAY_ID");
                    await GatewaySession.RunAsync(
                        BrokerOrigin.Parse(RequiredEnvironment("POLE_ACCESS_BROKER")),
                        Environment.GetEnvironmentVariable("POLE_ACCESS_IDENTITY_FILE") ??
                        "/run/pole-access/identity.pfx",
                        lifetime.Token);
                    return 0;
                default:
                    Console.Error.WriteLine("Usage: Pole.AccessGateway gateway");
                    return 2;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception)
        {
            // Paths, tokens, certificates, HTTP bodies and provider errors are not diagnostics.
            Console.Error.WriteLine("Access Gateway failed. Check its configuration and broker status.");
            return 1;
        }
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException("Required gateway configuration is missing.");
}
