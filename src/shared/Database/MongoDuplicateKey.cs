using Pole.Architecture;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Database;

/// <summary>Classifies only the expected unique index; unrelated duplicates remain infrastructure errors.</summary>
[ViolateArchitecture(ArchitectureRules.RawBson,
    "Driver duplicate-key details and command results have dynamic BSON metadata; preserve " +
    "malformed-field fallback and exact index/collection matching.")]
public static partial class MongoDuplicateKey
{
    private const int DuplicateKeyCode = 11000;

    public static bool IsForIdIndex(Exception exception, string collectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        return IsForIndex(exception, "_id_", collectionName);
    }

    public static bool IsForIndex(Exception exception, string indexName,
        string? collectionName = null) => exception switch
        {
            MongoWriteException
            {
                WriteError.Category: ServerErrorCategory.DuplicateKey,
                WriteConcernError: null
            } write =>
                Matches(write.WriteError.Details, write.WriteError.Message, indexName, collectionName),
            MongoCommandException command when command.Code == DuplicateKeyCode
                && !command.Result.Contains("writeConcernError") =>
                Matches(command.Result, command.ErrorMessage, indexName, collectionName),
            _ => false
        };

    public static bool IsForIndex<T>(MongoBulkWriteException<T> exception, string indexName,
        string? collectionName = null) =>
        exception.WriteConcernError is null && exception.WriteErrors.Count > 0 &&
        exception.WriteErrors.All(error => error.Category == ServerErrorCategory.DuplicateKey &&
            Matches(error.Details, error.Message, indexName, collectionName));

    private static bool Matches(BsonDocument? details, string message, string expected, string? collectionName)
    {
        if (collectionName is not null)
        {
            var collection = CollectionMarker().Match(message);
            if (!collection.Success || !collection.Groups[1].Value.EndsWith("." + collectionName,
                StringComparison.Ordinal))
                return false;
        }
        if (details is not null && details.TryGetValue("indexName", out var index) && index.IsString)
            return string.Equals(index.AsString, expected, StringComparison.Ordinal);
        // MongoDB's duplicate-key reply commonly supplies keyPattern/keyValue but no index name.
        // Parse its exact index marker; a substring anywhere in the exception is insufficient.
        var match = IndexMarker().Match(message);
        return match.Success && string.Equals(match.Groups[1].Value, expected, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^E11000 duplicate key error collection:\s+(\S+)\s+index:", RegexOptions.CultureInvariant)]
    private static partial Regex CollectionMarker();

    [GeneratedRegex(
        @"^E11000 duplicate key error collection:\s+\S+\s+index:\s+(\S+)(?:\s+collation:\s+\{[^}]*\})?\s+dup key:",
        RegexOptions.CultureInvariant)]
    private static partial Regex IndexMarker();
}
