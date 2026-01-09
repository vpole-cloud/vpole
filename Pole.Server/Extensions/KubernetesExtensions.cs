using System.Net;
using k8s;
using k8s.Autorest;

namespace Pole.Server.Extensions;

public static class KubernetesExtensions
{
    public static bool IsAlreadyExists(HttpOperationException ex)
        => ex.Response?.StatusCode == HttpStatusCode.Conflict;

    public static async Task CreateOrIgnoreAsync(Func<Task> createCall)
    {
        try
        {
            await createCall();
        }
        catch (HttpOperationException ex) when (IsAlreadyExists(ex))
        {
            // create-only semantics
        }
    }
    
    extension (IKubernetes kubernetes)
    {
    }
    
    
}