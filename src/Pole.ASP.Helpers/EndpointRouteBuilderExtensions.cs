using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Pole.ASP.Helpers;

public enum EndpointHttpMethod
{
    Get,
    Post,
    Put,
    Delete,
    Patch
}

public static class EndpointRouteBuilderExtensions
{
    public static RouteHandlerBuilder MapMethod<THandler>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        string methodName,
        EndpointHttpMethod httpMethod)
    {
        var handlerMethod = typeof(THandler).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method '{methodName}' was not found on type '{typeof(THandler).FullName}'.");

        var parameterTypes = handlerMethod
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToList();

        parameterTypes.Add(handlerMethod.ReturnType);

        var delegateType = Expression.GetDelegateType(parameterTypes.ToArray());
        var handler = handlerMethod.CreateDelegate(delegateType);

        return httpMethod switch
        {
            EndpointHttpMethod.Get => endpoints.MapGet(pattern, handler),
            EndpointHttpMethod.Post => endpoints.MapPost(pattern, handler),
            EndpointHttpMethod.Put => endpoints.MapPut(pattern, handler),
            EndpointHttpMethod.Delete => endpoints.MapDelete(pattern, handler),
            EndpointHttpMethod.Patch => endpoints.MapPatch(pattern, handler),
            _ => throw new ArgumentOutOfRangeException(nameof(httpMethod), httpMethod, "Unsupported HTTP method.")
        };
    }
}
