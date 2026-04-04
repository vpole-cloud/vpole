using Pole.AppHost.MongoDb;

var builder = DistributedApplication.CreateBuilder(args);

var mongoUsername = builder.AddParameter("mongo-username");
var mongoPassword = builder.AddParameter("mongo-password", secret: true);
var mongoServer = builder.AddMongoDB("pole-mongo", userName: mongoUsername, password: mongoPassword)
    .WithBindMount("MongoDb/mongo-entrypoint.sh", "/usr/local/bin/mongo-entrypoint.sh", isReadOnly: true)
    .WithEntrypoint("/usr/local/bin/mongo-entrypoint.sh")
    .WithMongoExpress(options =>
    {
        options.WithLifetime(ContainerLifetime.Persistent);
    })
    .WithEndpoint("tcp", endpoint =>
    {
        endpoint.Port = 27017;
        endpoint.TargetPort = 27017;
        endpoint.IsProxied = false;
    }, createIfNotExists: true)
    .WithDataVolume("pole-mongo-data")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithReplicaSet();
var database = mongoServer.AddDatabase("pole-database-base");
var mongoReplicaSet = builder
    .AddMongoReplicaSet("pole-database", database.Resource);

var keycloakUsername = builder.AddParameter("keycloak-username");
var keycloakPassword = builder.AddParameter("keycloak-password", secret: true);
var keycloak = builder.AddKeycloak("pole-keycloak",8080, keycloakUsername, keycloakPassword)
    .WithRealmImport("../../infrastructure/keycloak/realms/pole.control-plane.realm.json")
    .WithDataVolume("pole-keycloak-data")
    .WithLifetime(ContainerLifetime.Persistent);

var rabbitMqUsername = builder.AddParameter("rabbitmq-username");
var rabbitMqPassword = builder.AddParameter("rabbitmq-password", secret: true);
var rabbitMq = builder.AddRabbitMQ("pole-rabbitmq", rabbitMqUsername, rabbitMqPassword)
    .WithManagementPlugin()
    .WithDataVolume("pole-rabbitmq-data")
    .WithLifetime(ContainerLifetime.Persistent);

var api = builder.AddProject<Projects.Pole_Server>("pole-server")
    .WithReference(keycloak)
    .WithReference(mongoServer)
    .WithReference(database)
    .WithReference(mongoReplicaSet)
    .WithReference(rabbitMq)
    .WaitFor(keycloak)
    .WaitFor(mongoServer)
    .WaitFor(mongoReplicaSet)
    .WaitFor(database)
    .WaitFor(rabbitMq);

var blog = builder.AddProject<Projects.Blog>("blog")
    .WithReference(api)
    .WaitFor(api)
    .WithExplicitStart();

var frontend = builder.AddViteApp("frontend", "../frontend")
    .WithEndpoint("http", (endpointAnnotation) =>
    {
        endpointAnnotation.Port = 5173;
    })
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
