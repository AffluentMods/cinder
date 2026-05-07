using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Cinder.Core.Cases;

/// <summary>
/// Opens / creates Cinder case databases. Each case is a single SQLite file. Schema migrations
/// live as embedded SQL resources under <c>Sql/Migrations/</c> and are applied on
/// <see cref="Migrate"/>. Migration filenames sort lexically (e.g. <c>0001_initial.sql</c>);
/// applied versions are tracked in the <c>schema_migrations</c> table inside the case file.
/// </summary>
public sealed class CaseStore
{
    /// <summary>Latest schema version known to this build. Bumped when a new migration ships.</summary>
    public const int CurrentSchemaVersion = 1;

    private const string MigrationResourcePrefix = "Cinder.Core.Sql.Migrations.";

    private readonly string _connectionString;

    public CaseStore(string casePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(casePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = casePath,
            ForeignKeys = true,
            Pooling = true,
        }.ToString();
    }

    public string ConnectionString => _connectionString;

    /// <summary>Apply all pending migrations. Returns the resulting schema version.</summary>
    public int Migrate()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        conn.Execute(
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                name        TEXT    PRIMARY KEY,
                applied_utc TEXT    NOT NULL
            );
            """,
            transaction: tx);

        var applied = conn.Query<string>(
            "SELECT name FROM schema_migrations;",
            transaction: tx).ToHashSet(StringComparer.Ordinal);

        var migrations = LoadMigrations();
        foreach (var (name, sql) in migrations)
        {
            if (applied.Contains(name))
            {
                continue;
            }

            conn.Execute(sql, transaction: tx);
            conn.Execute(
                "INSERT INTO schema_migrations (name, applied_utc) VALUES (@Name, @Now);",
                new { Name = name, Now = DateTimeOffset.UtcNow.ToString("O") },
                transaction: tx);
        }

        tx.Commit();
        return CurrentSchemaVersion;
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static IReadOnlyList<(string Name, string Sql)> LoadMigrations()
    {
        var asm = typeof(CaseStore).Assembly;
        var resourceNames = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(MigrationResourcePrefix, StringComparison.Ordinal) &&
                        n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var migrations = new List<(string, string)>(resourceNames.Count);
        foreach (var name in resourceNames)
        {
            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Migration resource missing: {name}");
            using var reader = new StreamReader(stream);
            migrations.Add((name[MigrationResourcePrefix.Length..], reader.ReadToEnd()));
        }
        return migrations;
    }
}
