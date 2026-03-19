using System.Net;
using k8s.Autorest;

namespace Pole.Features.Core.Kubernetes;

public static class KubernetesExtensions
{
    public static async Task CreateIfNotExistAsync(Func<Task> createCall)
    {
        try
        {
            await createCall();
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            // create-only semantics
        }
    }
}