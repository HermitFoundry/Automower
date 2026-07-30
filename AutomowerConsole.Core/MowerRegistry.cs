using Dapper;
using Microsoft.Data.Sqlite;

namespace AutomowerConsole.Core;

// Storage seam for the mower catalog (mowers.json) - shared across all
// mowers, unlike IMowerRepository which is scoped to one. Kept as its own
// interface so a future SQLite "common" database (registry + whatever else
// isn't mower-specific) can implement this independently of the per-mower
// databases.
public interface IMowerRegistry
{
    List<StoredMower>? LoadMowers();
    void SaveMowers(IEnumerable<StoredMower> mowers);
}

public class JsonlMowerRegistry : IMowerRegistry
{
    public List<StoredMower>? LoadMowers() => Storage.LoadMowers();
    public void SaveMowers(IEnumerable<StoredMower> mowers) => Storage.SaveMowers(mowers);
}

// SQLite-backed IMowerRegistry - the common.db counterpart to
// SqliteMowerRepository's per-mower .db files (see the 2026-07-30
// storage-migration plan). A connection is opened fresh per call, same
// reasoning as SqliteMowerRepository.
public class SqliteMowerRegistry : IMowerRegistry
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS Mowers (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            Model TEXT NOT NULL,
            SerialNumber INTEGER NOT NULL
        );
        """;

    private SqliteConnection OpenConnection()
    {
        Storage.EnsureDataDir();
        var connection = new SqliteConnection($"Data Source={Storage.GetCommonDbPath()}");
        connection.Open();
        connection.Execute("PRAGMA journal_mode=WAL;");
        connection.Execute(SchemaSql);
        return connection;
    }

    // Empty (not null) when the table has no rows, matching JsonlMowerRegistry/
    // MowerService's own "empty means not cached yet, go fetch from the API"
    // contract - null is reserved for genuinely no data source to check at all,
    // which doesn't apply here (the table always exists once queried).
    public List<StoredMower>? LoadMowers()
    {
        using var connection = OpenConnection();
        return connection.Query<StoredMower>("SELECT Id, Name, Model, SerialNumber FROM Mowers;").ToList();
    }

    public void SaveMowers(IEnumerable<StoredMower> mowers)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        connection.Execute("DELETE FROM Mowers;", transaction: transaction);
        connection.Execute(
            "INSERT INTO Mowers (Id, Name, Model, SerialNumber) VALUES (@Id, @Name, @Model, @SerialNumber);",
            mowers, transaction);
        transaction.Commit();
    }
}
