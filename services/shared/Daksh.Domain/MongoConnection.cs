using MongoDB.Driver;

namespace Daksh.Domain;

/// <summary>
/// Shared MongoDB connection. No collections/schema are modeled yet — this only exposes a
/// working <see cref="IMongoDatabase"/> for future work to build on incrementally.
/// </summary>
public static class MongoConnection
{
    public static IMongoDatabase CreateDatabase(string connectionString, string databaseName)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Mongo:ConnectionString (Mongo__ConnectionString) is required.");
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new InvalidOperationException("Mongo:Database (Mongo__Database) is required.");

        var client = new MongoClient(connectionString);
        return client.GetDatabase(databaseName);
    }
}
