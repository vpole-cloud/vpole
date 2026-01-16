var builder = DistributedApplication.CreateBuilder(args);

var mongoServer = builder.AddMongoDB("pole-mongo")
    .WithDataVolume("pole-mongo-data")
    .WithLifetime(ContainerLifetime.Persistent);
var database = mongoServer.AddDatabase("pole-database");

var username = builder.AddParameter("keycloak-username");
var password = builder.AddParameter("keycloak-password", secret: true);
var keycloak = builder.AddKeycloak("pole-keycloak",8080, adminUsername: username, adminPassword: password)
    .WithDataVolume("pole-keycloak-data")
    .WithLifetime(ContainerLifetime.Persistent);

var rabbitMq = builder.AddRabbitMQ("pole-rabbitmq")
    .WithDataVolume("pole-rabbitmq-data")
    .WithLifetime(ContainerLifetime.Persistent);

builder.AddProject<Projects.Pole_Server>("pole-server")
    .WithReference(keycloak)
    .WithReference(mongoServer)
    .WithReference(rabbitMq)
    .WaitFor(keycloak)
    .WaitFor(mongoServer)
    .WaitFor(rabbitMq);

builder.Build().Run();
