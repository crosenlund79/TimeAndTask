using Microsoft.Data.Sqlite;

namespace WorkTrace.Infrastructure.Persistence;

public static class SqliteConnectionStrings
{
    /// <summary>Connection string for the WorkTrace database with foreign keys enforced.</summary>
    public static string ForFile(string databasePath) => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        ForeignKeys = true,
        Pooling = true,
        DefaultTimeout = 30,
    }.ToString();
}
