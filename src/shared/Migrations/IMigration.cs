using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Migrations;

public interface IMigration
{
    /// <summary>
    /// A unique identifier for the migration
    /// </summary>
    ObjectId Id { get; }
    
    /// <summary>
    /// A human-readable name for the migration, used for logging and
    /// documentation purposes
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// A description of the migration, explaining what changes it makes to the
    /// database and why it is necessary
    /// </summary>
    string Description { get; }
    
    /// <summary>
    /// The method that performs the migration, applying the necessary changes
    /// to the database context. Runner will ensure that each migration is run once.
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task Up(IMongoDatabase context, CancellationToken cancellationToken = default);
}