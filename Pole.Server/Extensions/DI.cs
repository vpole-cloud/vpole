using System.Reflection;
using k8s;
using MassTransit;
using Pole.Server.Database;
using Pole.Server.Mediator;

namespace Pole.Server.Extensions;

public static class DI
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddPoleMediator(IConfiguration configuration, Assembly assembly)
        {
            services.AddSingleton<IMediator, Mediator.Mediator>();

            var handlerTypes = assembly.GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces()
                    .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMediatorHandler<,>)));

            foreach (var handlerType in handlerTypes)
            {
                var interfaceType = handlerType.GetInterfaces()
                    .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMediatorHandler<,>));
                services.AddScoped(interfaceType, handlerType);
            }

            return services;
        }

        public IServiceCollection AddKubernetes(IConfiguration configuration)
        {
            services.AddSingleton<IKubernetes>(_ =>
            {
                var config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
                return new Kubernetes(config);
            });
            return services;
        }
        public IServiceCollection AddEventBus(IConfiguration configuration, Assembly assembly)
        {
            services.AddMassTransit(x =>
            {
                x.UsingRabbitMq((context, cfg) =>
                {
                    cfg.Host(configuration.GetConnectionString("pole-rabbitmq"));
                    cfg.ConfigureEndpoints(context);
                });
                x.AddConsumers(assembly);
            });
            return services;
        }
        
        public IServiceCollection AddDatabase()
        {
            services.AddSingleton<MongoDbContext>();
            return services;
        }
    }
}