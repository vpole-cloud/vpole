using System.Diagnostics;
using System.Text;
using System.Text.Json;
using k8s;

namespace Pole.Tests.E2E.Infrastructure;

public sealed partial class PoleE2EFixture
{
    internal Task<JsonElement> ReadMetadataTestResourcesAsync(
        string groupId,
        string kind,
        CancellationToken ct) => ReadKubectlJsonAsync(
        ["get", kind, "--namespace", GetNamespaceName(groupId), "--output=json"],
        ct);

    internal async Task<bool> MetadataCredentialExistsAsync(
        string groupId,
        string name,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_kubernetesTarget.KubectlPath)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            },
        };
        foreach (var argument in _kubernetesTarget.KubectlSelectionArguments.Concat(
                     new[]
                     {
                         "get", "secret", name, "-n", GetNamespaceName(groupId), "--ignore-not-found", "-o", "name"
                     }))
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        try
        {
            var names = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await errors;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("The metadata credential identity query failed.");
            var result = (await names).Trim();
            if (result.Length > 0 && result != $"secret/{name}")
                throw new InvalidOperationException("The metadata credential query returned an unexpected identity.");
            return result.Length > 0;
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    internal async Task<bool> VerifyMetadataDatabaseIsolationAsync(
        string groupId,
        string postgresId,
        string credentialName,
        string ownDatabase,
        string siblingDatabase,
        CancellationToken ct)
    {
        var ns = GetNamespaceName(groupId);
        var secret = await _kubernetes.CoreV1.ReadNamespacedSecretAsync(credentialName, ns, cancellationToken: ct);
        var username = Encoding.UTF8.GetString(secret.Data["username"]);
        var password = Encoding.UTF8.GetString(secret.Data["password"]);
        var cluster = await ReadKubectlJsonAsync(["get", "cluster", $"pg-{postgresId}", "-n", ns, "-o", "json"], ct);
        var primary = cluster.GetProperty("status").GetProperty("currentPrimary").GetString()!;
        var pod = await _kubernetes.CoreV1.ReadNamespacedPodAsync(primary, ns, cancellationToken: ct);
        var address = pod.Status.PodIP;

        async Task<bool> ProbeAsync(string database, bool expectDenied)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(_kubernetesTarget.KubectlPath)
                {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            foreach (var argument in _kubernetesTarget.KubectlSelectionArguments.Concat(
                         new[]
                         {
                             "exec", "-i", "-n", ns, primary, "-c", "postgres", "--", "sh", "-c",
                             "IFS= read -r PGPASSWORD; export PGPASSWORD; " +
                             "exec psql -XAtq --set=ON_ERROR_STOP=1 -h \"$1\" -U \"$2\" -d \"$3\" -c 'SELECT current_user;'",
                             "metadata-isolation-probe", address, username, database,
                         }))
                process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            try
            {
                var rows = process.StandardOutput.ReadToEndAsync(ct);
                var errors = process.StandardError.ReadToEndAsync(ct);
                await process.StandardInput.WriteLineAsync(password.AsMemory(), ct);
                process.StandardInput.Close();
                await process.WaitForExitAsync(ct);
                var result = await rows;
                var diagnostics = await errors;
                return expectDenied
                    ? process.ExitCode != 0 && diagnostics.Contains(
                        "permission denied for database",
                        StringComparison.Ordinal)
                    : process.ExitCode == 0 && result.Trim() == username;
            }
            finally
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
        }

        return await ProbeAsync(ownDatabase, expectDenied: false) &&
            await ProbeAsync(siblingDatabase, expectDenied: true);
    }
}
