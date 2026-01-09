var builder = DistributedApplication.CreateBuilder(args);

var mongoServer = builder.AddMongoDB("pole-mongo")
    .WithDataVolume("pole-mongo-data")
    .WithLifetime(ContainerLifetime.Persistent);
var database = mongoServer.AddDatabase("pole-database");
var rabbitMq = builder.AddRabbitMQ("pole-rabbitmq")
    .WithDataVolume("pole-rabbitmq-data")
    .WithLifetime(ContainerLifetime.Persistent);

builder.AddProject<Projects.Pole_Server>("pole-server")
    .WithReference(database)
    .WithReference(rabbitMq)
    .WaitFor(database)
    .WaitFor(rabbitMq);

builder.Build().Run();
