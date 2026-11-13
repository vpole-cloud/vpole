using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using k8s;
using k8s.Models;
using Pole.Testing;

namespace Pole.Tests.E2E.Infrastructure;

public sealed partial class PoleE2EFixture
{
    public sealed record BackupTestStorage(
        string Endpoint,
        string AccessKey,
        string SecretKey
    )
    {
        public override string ToString() => "BackupTestStorage([REDACTED])";
    }

    public async Task<BackupTestStorage> CreateBackupTestStorageAsync(TestResourceScope scope, CancellationToken ct)
    {
        var ns = await _kubernetes.CoreV1.CreateNamespaceAsync(
            new V1Namespace
            {
                Metadata = new V1ObjectMeta
                {
                    Name = $"pole-{Identity.WorktreeId}-backup-{Guid.NewGuid().ToString("N")[..8]}",
                    Labels = Identity.NamespaceLabels.ToDictionary(),
                },
            },
            cancellationToken: ct);
        var name = ns.Metadata.Name;
        scope.Defer(
            "remove isolated Barman test object storage",
            async cleanup =>
            {
                var existing = await _kubernetes.CoreV1.ReadNamespaceAsync(name, cancellationToken: cleanup);
                if (existing.Metadata.Uid != ns.Metadata.Uid || Identity.NamespaceLabels.Any(pair =>
                        !existing.Metadata.Labels.TryGetValue(pair.Key, out var value) || value != pair.Value))
                    throw new InvalidOperationException("S3 fixture namespace ownership changed.");
                await _kubernetes.CoreV1.DeleteNamespaceAsync(
                    name,
                    body: new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = ns.Metadata.Uid } },
                    cancellationToken: cleanup);
            });
        var access = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await _kubernetes.CoreV1.CreateNamespacedSecretAsync(
            new V1Secret
            {
                Metadata = new V1ObjectMeta { Name = "archive-access" },
                StringData = new Dictionary<string, string> { ["access"] = access, ["secret"] = secret },
            },
            name,
            cancellationToken: ct);
        await _kubernetes.CoreV1.CreateNamespacedPodAsync(
            new V1Pod
            {
                Metadata = new V1ObjectMeta { Name = "s3", Labels = new Dictionary<string, string> { ["app"] = "s3" } },
                Spec = new V1PodSpec
                {
                    AutomountServiceAccountToken = false,
                    SecurityContext = new V1PodSecurityContext { FsGroup = 10001 },
                    Containers =
                    [
                        new V1Container
                        {
                            Name = "s3", Image = "ghcr.io/rustfs/rustfs:1.0.5", Args = ["/data"],
                            Env =
                            [
                                FromSecret("RUSTFS_ACCESS_KEY", "access"), FromSecret("RUSTFS_SECRET_KEY", "secret"),
                                new V1EnvVar { Name = "RUSTFS_ADDRESS", Value = ":9000" },
                                new V1EnvVar { Name = "RUSTFS_CONSOLE_ENABLE", Value = "false" },
                            ],
                            VolumeMounts = [new V1VolumeMount { Name = "data", MountPath = "/data" }],
                        },
                    ],
                    Volumes = [new V1Volume { Name = "data", EmptyDir = new V1EmptyDirVolumeSource() }],
                },
            },
            name,
            cancellationToken: ct);
        var service = await _kubernetes.CoreV1.CreateNamespacedServiceAsync(
            new V1Service
            {
                Metadata = new V1ObjectMeta { Name = "s3" },
                Spec = new V1ServiceSpec
                {
                    Type = "NodePort", Selector = new Dictionary<string, string> { ["app"] = "s3" },
                    Ports = [new V1ServicePort { Name = "s3", Port = 9000, TargetPort = 9000 }],
                },
            },
            name,
            cancellationToken: ct);
        var port = service.Spec.Ports.Single().NodePort!.Value;
        await TestWait.UntilAsync(
            async token =>
                (await _kubernetes.CoreV1.ReadNamespacedPodAsync("s3", name, cancellationToken: token)).Status.Phase ==
                "Running",
            TimeSpan.FromMinutes(3),
            "S3 test pod did not start.",
            ct);
        var forward = await KubernetesPortForwardSession.StartAsync(
            _kubernetesTarget,
            name,
            "service/s3",
            9000,
            ct,
            port);
        scope.Defer("stop test S3 loopback forwarding", _ => forward.DisposeAsync().AsTask());
        // The full lab already resolves this name to the primary node in Pods and to loopback on the host.
        var endpoint = $"http://harbor.pole.localhost:{port}";
        using var client = new AmazonS3Client(
            new BasicAWSCredentials(access, secret),
            new AmazonS3Config
            {
                ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1",
            });
        await TestWait.UntilAsync(
            async token =>
            {
                try
                {
                    await client.PutBucketAsync(new PutBucketRequest { BucketName = "backups" }, token);
                    return true;
                }
                catch (AmazonS3Exception error) when ((int)error.StatusCode >= 500)
                {
                    return false;
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            },
            TimeSpan.FromMinutes(2),
            "S3 bucket did not become available.",
            ct);
        return new BackupTestStorage(endpoint, access, secret);
    }

    private static V1EnvVar FromSecret(string name, string key) => new()
    {
        Name = name,
        ValueFrom = new V1EnvVarSource
            { SecretKeyRef = new V1SecretKeySelector { Name = "archive-access", Key = key } },
    };

    public async Task WaitForUnreachableRecoveryFailureAsync(
        string group,
        string resourceId,
        CancellationToken ct)
    {
        var ns = GetNamespaceName(group);
        var name = "pg-" + resourceId;
        var rejectedJobs = new HashSet<string>(StringComparer.Ordinal);
        await TestWait.UntilAsync(
            async token =>
            {
                var sources = await ReadKubectlJsonAsync(
                    [
                        "get", "clusters.postgresql.cnpg.io", "-n", ns, "-l", "pole.io/resource-id=" + resourceId, "-o",
                        "json"
                    ],
                    token);
                var cluster = sources.GetProperty("items")
                    .EnumerateArray()
                    .SingleOrDefault(item => item.GetProperty("metadata").GetProperty("name").GetString() == name);
                if (cluster.ValueKind == JsonValueKind.Undefined)
                    return false;
                var clusterUid = cluster.GetProperty("metadata").GetProperty("uid").GetString();
                var jobs = await _kubernetes.BatchV1.ListNamespacedJobAsync(
                    ns,
                    labelSelector: "cnpg.io/cluster=" + name + ",cnpg.io/jobRole=full-recovery",
                    cancellationToken: token);
                var job = jobs.Items.SingleOrDefault(item => item.Metadata.OwnerReferences?.Any(owner =>
                    owner.Kind == "Cluster" && owner.Name == name && owner.Uid == clusterUid) == true);
                if (job is null)
                    return false;
                if (!rejectedJobs.Contains(job.Metadata.Uid))
                {
                    var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync(
                        ns,
                        labelSelector: "batch.kubernetes.io/controller-uid=" + job.Metadata.Uid,
                        cancellationToken: token);
                    foreach (var pod in pods.Items.Where(p =>
                                 p.Status.Phase == "Failed" && p.Metadata.OwnerReferences?.Any(owner =>
                                     owner.Kind == "Job" && owner.Uid == job.Metadata.Uid) == true))
                    {
                        using var logs = await _kubernetes.CoreV1.ReadNamespacedPodLogAsync(
                            pod.Metadata.Name,
                            ns,
                            container: pod.Spec.Containers.Single().Name,
                            limitBytes: 524288,
                            cancellationToken: token);
                        using var reader = new StreamReader(logs);
                        if (!(await reader.ReadToEndAsync(token)).Contains(
                                "recovery ended before configured recovery target was reached",
                                StringComparison.Ordinal))
                            continue;

                        rejectedJobs.Add(job.Metadata.Uid);
                        break;
                    }
                }

                return rejectedJobs.Contains(job.Metadata.Uid) && job.Status?.Conditions?.Any(condition =>
                    condition.Status == "True" && condition.Type is "Failed" or "FailureTarget") == true;
            },
            TimeSpan.FromMinutes(15),
            "The native recovery Job did not finish its default retry lifecycle after PostgreSQL rejected the target.",
            ct,
            TimeSpan.FromSeconds(3));
    }

    public async Task WaitForBackupCredentialVersionAsync(
        string group,
        string archiveId,
        long version,
        CancellationToken ct)
    {
        var ns = GetNamespaceName(group);
        var store = await ReadKubectlJsonAsync(
            ["get", "objectstores.barmancloud.cnpg.io", "archive-" + archiveId, "-n", ns, "-o", "json"],
            ct);
        var name = store.GetProperty("spec")
            .GetProperty("configuration")
            .GetProperty("s3Credentials")
            .GetProperty("accessKeyId")
            .GetProperty("name")
            .GetString()!;
        await TestWait.UntilAsync(
            async token =>
            {
                var projectedVersion = await ReadKubectlJsonAsync(
                    [
                        "get", "secret", name, "-n", ns, "-o",
                        @"jsonpath={.metadata.annotations.pole\.io/secret-version}"
                    ],
                    token);
                return projectedVersion.GetInt64() == version;
            },
            TimeSpan.FromMinutes(2),
            "The rotated archive Secret was not projected.",
            ct,
            TimeSpan.FromSeconds(2));
    }

    public async Task WaitForWalArchiveHealthAsync(
        string group,
        string sourceId,
        string password,
        bool healthy,
        CancellationToken ct)
    {
        var nextWalWrite = DateTimeOffset.MinValue;
        await TestWait.UntilAsync(
            async token =>
            {
                // Barman caches Secret reads briefly. Keep creating WAL until an archive command
                // observes the rotated version, rather than relying on a single switch within that cache window.
                if (DateTimeOffset.UtcNow >= nextWalWrite)
                {
                    await BackupSqlAsync(
                        group,
                        sourceId,
                        password,
                        "postgres",
                        "SELECT pg_create_restore_point('test_credential_rotation_probe'); SELECT pg_switch_wal();",
                        token);
                    nextWalWrite = DateTimeOffset.UtcNow.AddSeconds(15);
                }

                var source = await ReadKubectlJsonAsync(
                    ["get", "cluster", "pg-" + sourceId, "-n", GetNamespaceName(group), "-o", "json"],
                    token);
                return source.GetProperty("status")
                    .GetProperty("conditions")
                    .EnumerateArray()
                    .Any(condition => condition.GetProperty("type").GetString() == "ContinuousArchiving" &&
                        condition.GetProperty("status").GetString() == (healthy ? "True" : "False"));
            },
            TimeSpan.FromMinutes(3),
            "WAL archive status did not reflect the rotated credential.",
            ct,
            TimeSpan.FromSeconds(2));
    }

    public async Task<string> BackupFailureEvidenceAsync(
        string groupId,
        string sourceId,
        string operationId,
        CancellationToken ct)
    {
        var ns = GetNamespaceName(groupId);
        var backup = await ReadKubectlJsonAsync(["get", "backup", "backup-" + operationId, "-n", ns, "-o", "json"], ct);
        var cluster = await ReadKubectlJsonAsync(["get", "cluster", "pg-" + sourceId, "-n", ns, "-o", "json"], ct);
        var phase = backup.GetProperty("status").GetProperty("phase").GetString() switch
        {
            "failed" => "failed",
            "walArchivingFailing" => "walArchivingFailing",
            "invalid backup definition" => "invalid backup definition",
            _ => "unknown",
        };
        var evidence = new List<string> { backup.GetProperty("status").GetRawText() };
        var primary = cluster.GetProperty("status").GetProperty("currentPrimary").GetString()!;
        var pod = await _kubernetes.CoreV1.ReadNamespacedPodAsync(primary, ns, cancellationToken: ct);
        var statuses = (pod.Status.ContainerStatuses ?? []).Concat(pod.Status.InitContainerStatuses ?? []).ToArray();
        var sidecar = statuses.FirstOrDefault(status => status.Name == "plugin-barman-cloud");
        foreach (var container in statuses.Where(status => status.Name is "postgres" or "plugin-barman-cloud"))
        {
            using var stream = await _kubernetes.CoreV1.ReadNamespacedPodLogAsync(
                primary,
                ns,
                container: container.Name,
                limitBytes: 262144,
                sinceSeconds: 600,
                cancellationToken: ct);
            using var reader = new StreamReader(stream);
            evidence.Add(await reader.ReadToEndAsync(ct));
        }

        // Logs and status can include client credentials. Only fixed diagnostic categories may leave this method.
        string[] markers =
        [
            "could not connect", "connection refused", "not found", "unknown plugin", "no route to host",
            "could not resolve", "endpoint connection", "credential", "no such host", "accessdenied",
            "invalidaccesskeyid",
            "signaturedoesnotmatch", "nosuchbucket", "forbidden", "could not translate host", "timeout",
            "no space left",
            "wal archiving is not working", "no such file", "plugin not found", "plugin is not", "permission denied",
            "error running barman", "while taking backup", "exit status", "connection reset", "ssl error", "rpc error",
            "temporary failure", "not registered", "error loading", "not yet configured", "no matching"
        ];
        var causes = markers.Where(marker =>
            evidence.Any(value => value.Contains(marker, StringComparison.OrdinalIgnoreCase)));
        return
            $"CNPG Backup phase={phase}, primary sidecar present={sidecar is not null}, ready={sidecar?.Ready}, markers=[{string.Join(", ", causes)}].";
    }

    public async Task<string> BackupSqlAsync(
        string groupId,
        string id,
        string password,
        string database,
        string sql,
        CancellationToken ct)
    {
        var ns = GetNamespaceName(groupId);
        var primary = string.Empty;
        var host = string.Empty;
        await TestWait.UntilAsync(
            async token =>
            {
                var native = await ReadKubectlJsonAsync(["get", "cluster", $"pg-{id}", "-n", ns, "-o", "json"], token);
                if (!native.TryGetProperty("status", out var status) ||
                    !status.TryGetProperty("currentPrimary", out var leader) ||
                    leader.GetString() is not { Length: > 0 } name ||
                    !status.TryGetProperty("conditions", out var conditions) || !conditions.EnumerateArray()
                        .Any(c => c.GetProperty("type").GetString() == "Ready" &&
                            c.GetProperty("status").GetString() == "True"))
                    return false;

                var pod = await _kubernetes.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: token);
                if (pod.Status.PodIP is not { Length: > 0 } address)
                    return false;
                if (await VerifyPostgresLoginAsync(new PostgresLoginProbe(ns, name, address, password), token) !=
                    PostgresLoginResult.Authenticated)
                    return false;
                primary = name;
                host = address;
                return true;
            },
            TimeSpan.FromMinutes(5),
            "CNPG did not expose a ready primary accepting its administrator credential.",
            ct,
            TimeSpan.FromSeconds(2));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_kubernetesTarget.KubectlPath)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var arg in _kubernetesTarget.KubectlSelectionArguments.Concat(
                     new[]
                     {
                         "exec", "-i", "-n", ns, primary, "-c", "postgres", "--", "sh", "-c",
                         "IFS= read -r PGPASSWORD; export PGPASSWORD; exec psql -XAtq --set=ON_ERROR_STOP=1 -h \"$1\" -U postgres -d \"$2\"",
                         "backup-data-probe", host, database,
                     }))
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        try
        {
            var rows = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            await process.StandardInput.WriteLineAsync(password.AsMemory(), ct);
            await process.StandardInput.WriteLineAsync(sql.AsMemory(), ct);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            await errors;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("PostgreSQL backup data probe failed.");
            return (await rows).Trim();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
