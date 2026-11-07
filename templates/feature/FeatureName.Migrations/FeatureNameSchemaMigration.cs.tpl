using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.FeatureName.Migrations;

public sealed class FeatureNameSchemaMigration : IMigration
{
    public ObjectId Id => new("__MIGRATION_ID__");
    public string Name => "FeatureName initial schema and tenant-name uniqueness";
    public string Description => "Creates the frozen resource schema and tenant-local unique name index.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        const string collection = "FeatureName";
        await context.CreateCollectionAsync(collection, new CreateCollectionOptions<BsonDocument>
        {
            Validator = new BsonDocument("$jsonSchema", new BsonDocument
            {
                { "bsonType", "object" },
                { "required", new BsonArray { "_id", "TenantId", "Details", "Revision", "Audit" } },
                { "properties", new BsonDocument
                    {
                        { "_id", new BsonDocument("bsonType", "objectId") },
                        { "TenantId", new BsonDocument("bsonType", "objectId") },
                        { "Revision", new BsonDocument { { "bsonType", "long" }, { "minimum", 1L } } },
                        { "Details", new BsonDocument
                            {
                                { "bsonType", "object" },
                                { "required", new BsonArray { "Name", "Description" } },
                                { "properties", new BsonDocument
                                    {
                                        { "Name", new BsonDocument
                                            { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", 80 } } },
                                        { "Description", new BsonDocument
                                            { { "bsonType", "string" }, { "maxLength", 500 } } }
                                    }
                                }
                            }
                        },
                        { "Audit", new BsonDocument
                            {
                                { "bsonType", "object" },
                                { "required", new BsonArray { "CreatedAtUtc", "UpdatedAtUtc" } },
                                { "properties", new BsonDocument
                                    {
                                        { "CreatedAtUtc", new BsonDocument("bsonType", "date") },
                                        { "UpdatedAtUtc", new BsonDocument("bsonType", "date") }
                                    }
                                }
                            }
                        }
                    }
                }
            })
        }, cancellationToken);
        await context.GetCollection<BsonDocument>(collection).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys
                .Ascending("TenantId").Ascending("Details.Name"),
                new CreateIndexOptions { Name = "UX_FeatureName_Tenant_Name", Unique = true }),
            cancellationToken: cancellationToken);
    }
}
