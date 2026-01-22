var builder = DistributedApplication.CreateBuilder(args);

var mongoServer = builder.AddMongoDB("pole-mongo")
    .WithDataVolume("pole-mongo-data")
    .WithLifetime(ContainerLifetime.Persistent);
var database = mongoServer.AddDatabase("pole-database");

var username = builder.AddParameter("keycloak-username");
var password = builder.AddParameter("keycloak-password", secret: true);
var keycloak = builder.AddKeycloak("pole-keycloak",8080, username, password)
    .WithRealmImport("../../infrastructure/keycloak/realms/pole.control-plane.realm.json")
    .WithDataVolume("pole-keycloak-data")
    .WithLifetime(ContainerLifetime.Persistent);

var rabbitMq = builder.AddRabbitMQ("pole-rabbitmq")
    .WithDataVolume("pole-rabbitmq-data")
    .WithLifetime(ContainerLifetime.Persistent);

var api = builder.AddProject<Projects.Pole_Server>("pole-server")
    .WithReference(keycloak)
    .WithReference(mongoServer)
    .WithReference(rabbitMq)
    .WaitFor(keycloak)
    .WaitFor(mongoServer)
    .WaitFor(rabbitMq);

var frontend = builder.AddViteApp("frontend", "../frontend")
    .WithEndpoint("http", (endpointAnnotation) =>
    {
        endpointAnnotation.Port = 5173;
    })
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
