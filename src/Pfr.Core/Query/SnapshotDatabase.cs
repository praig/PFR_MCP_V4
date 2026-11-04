using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Pfr.Core.Nios.Model;
using SQLitePCL;

namespace Pfr.Core.Query;

/// <summary>
/// A grid snapshot turned into an in-memory SQL database (SQLite) that queries run against. Nothing is written to
/// disk and nothing can change the stored snapshot: the database is a throwaway copy, opened read-only once loaded,
/// and only a single SELECT-type statement is accepted (<see cref="SqlGuard"/>). Queries that run too long are stopped.
/// Use from one thread at a time; <see cref="Execute"/> and <see cref="Dispose"/> wait for each other.
/// </summary>
public sealed class SnapshotDatabase : IDisposable
{
    /// <summary>How long a query may run before it is stopped.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    private SnapshotDatabase(SqliteConnection connection, IReadOnlyList<TableInfo> tables)
    {
        _connection = connection;
        Tables = tables;
    }

    /// <summary>The tables, with their columns and how many rows each holds.</summary>
    public IReadOnlyList<TableInfo> Tables { get; }

    /// <summary>The tables and columns without loading anything (row counts are 0).</summary>
    public static IReadOnlyList<TableInfo> Describe() => SnapshotSchema.Describe();

    /// <summary>Builds the database from a snapshot's inventory.</summary>
    public static SnapshotDatabase Load(GridInventory inventory)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        try
        {
            IpFunctions.Register(connection);

            var tables = new List<TableInfo>();
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var table in SnapshotSchema.Build(inventory))
                {
                    CreateTable(connection, transaction, table);
                    tables.Add(new TableInfo(table.Name, table.Description, table.Columns, Insert(connection, transaction, table)));
                }
                foreach (var index in SnapshotSchema.Indexes) Run(connection, transaction, index);
                transaction.Commit();
            }

            // From here on nothing can change the data.
            Run(connection, null, "PRAGMA query_only = ON");
            return new SnapshotDatabase(connection, tables);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs a read-only query and returns up to <paramref name="maxRows"/> rows. Named parameters in the SQL (:name)
    /// take their values from <paramref name="parameters"/>, which are bound as data, never put into the SQL text.
    /// </summary>
    /// <exception cref="QueryException">Not a single read-only statement, a parameter without a value, a mistake in the SQL, or it ran too long.</exception>
    public QueryResult Execute(string sql, int maxRows = 10_000, TimeSpan? timeout = null,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var statement = SqlGuard.Check(sql);
        var limit = timeout ?? DefaultTimeout;
        var tokens = QueryParameters.FindTokens(statement);
        foreach (var token in tokens)
        {
            if (parameters is null || !parameters.ContainsKey(QueryParameters.NameOf(token)))
                throw new QueryException($"The query uses {token} but no value was given for it.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var clock = Stopwatch.StartNew();
            StartTimer(limit);
            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = statement;
                foreach (var token in tokens)
                    command.Parameters.AddWithValue(token, parameters![QueryParameters.NameOf(token)] ?? DBNull.Value);
                using var reader = command.ExecuteReader();

                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                var rows = new List<object?[]>();
                var truncated = false;
                while (reader.Read())
                {
                    if (rows.Count >= maxRows)
                    {
                        truncated = true;
                        break;
                    }

                    var values = new object?[reader.FieldCount];
                    for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(values);
                }
                return new QueryResult(columns, rows, truncated, clock.Elapsed);
            }
            catch (SqliteException ex)
            {
                throw new QueryException(Explain(ex, limit), ex);
            }
            finally
            {
                StopTimer();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
    }

    /// <summary>Stops the running statement once the deadline passes (SQLite checks every few thousand steps).</summary>
    private void StartTimer(TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        raw.sqlite3_progress_handler(_connection.Handle, 5000, _ => DateTime.UtcNow > deadline ? 1 : 0, null);
    }

    private void StopTimer() => raw.sqlite3_progress_handler(_connection.Handle, 0, null, null);

    private static string Explain(SqliteException ex, TimeSpan limit)
    {
        if (ex.SqliteErrorCode == 9) // SQLITE_INTERRUPT
            return $"The query took longer than {limit.TotalSeconds:0} seconds and was stopped. Try a narrower query.";

        // "SQLite Error 1: 'no such table: x'." becomes "no such table: x".
        var message = ex.Message;
        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        if (message.StartsWith("SQLite Error", StringComparison.Ordinal) && colon >= 0) message = message[(colon + 2)..];
        return message.Trim().Trim('\'', '.').Trim();
    }

    private static void CreateTable(SqliteConnection connection, SqliteTransaction transaction, TableData table)
    {
        var columns = string.Join(", ", table.Columns.Select(c => $"\"{c.Name}\" {c.SqlType}"));
        Run(connection, transaction, $"CREATE TABLE \"{table.Name}\" ({columns})");
    }

    private static int Insert(SqliteConnection connection, SqliteTransaction transaction, TableData table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = Enumerable.Range(0, table.Columns.Count).Select(i => "$p" + i).ToArray();
        command.CommandText = $"INSERT INTO \"{table.Name}\" VALUES ({string.Join(",", names)})";
        var parameters = names.Select(name =>
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            command.Parameters.Add(parameter);
            return parameter;
        }).ToArray();

        var count = 0;
        foreach (var row in table.Rows)
        {
            for (var i = 0; i < parameters.Length; i++) parameters[i].Value = row[i] ?? DBNull.Value;
            command.ExecuteNonQuery();
            count++;
        }
        return count;
    }

    private static void Run(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
