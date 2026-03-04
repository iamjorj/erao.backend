using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Erao.Core.DTOs.Database;
using Erao.Core.Enums;
using Erao.Core.Interfaces;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MySql.Data.MySqlClient;
using Npgsql;
using System.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Oracle.ManagedDataAccess.Client;
using ClickHouse.Client.ADO;
using FirebirdSql.Data.FirebirdClient;
using DuckDB.NET.Data;
using Snowflake.Data.Client;

namespace Erao.Infrastructure.Services;

public class DatabaseQueryService : IDatabaseQueryService
{
    private readonly ILogger<DatabaseQueryService> _logger;

    // Regex to reject non-read-only SQL statements (case-insensitive, word-boundary)
    private static readonly Regex _dangerousSqlPattern = new(
        @"\b(ALTER|DROP|DELETE|INSERT|UPDATE|TRUNCATE|CREATE|EXEC|EXECUTE|GRANT|REVOKE|MERGE|COPY|EXPORT|ATTACH|DETACH)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> _allowedMongoDbCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "find", "aggregate", "count", "countDocuments", "distinct",
        "listCollections", "listDatabases", "dbStats", "collStats", "explain"
    };

    public DatabaseQueryService(ILogger<DatabaseQueryService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validates that the query is read-only (SELECT only). Throws if the query contains
    /// dangerous SQL statements.
    /// </summary>
    private static void ValidateReadOnlyQuery(string query)
    {
        if (_dangerousSqlPattern.IsMatch(query))
        {
            throw new InvalidOperationException(
                "Only SELECT queries are allowed. Data modification statements are not permitted.");
        }
    }

    /// <summary>
    /// Validates and sanitizes a SQL identifier (database name, table name, etc.)
    /// to prevent SQL injection. Only allows alphanumeric characters, underscores, dots, and hyphens.
    /// </summary>
    private static string ValidateIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("Identifier cannot be null or empty", nameof(identifier));

        var sanitized = Regex.Replace(identifier, @"[^a-zA-Z0-9_.\-]", "");
        if (string.IsNullOrEmpty(sanitized))
            throw new ArgumentException($"Invalid identifier: '{identifier}'", nameof(identifier));

        return sanitized;
    }

    /// <summary>
    /// Validates that a MongoDB command is read-only.
    /// Only allows: find, aggregate, count, countDocuments, distinct,
    /// listCollections, listDatabases, dbStats, collStats, explain.
    /// </summary>
    private static void ValidateMongoDbCommand(BsonDocument command)
    {
        if (command.ElementCount == 0)
            throw new InvalidOperationException("Empty MongoDB command.");

        var commandName = command.GetElement(0).Name;
        if (!_allowedMongoDbCommands.Contains(commandName))
        {
            throw new InvalidOperationException(
                $"MongoDB command '{commandName}' is not allowed. Only read-only commands are permitted: {string.Join(", ", _allowedMongoDbCommands)}");
        }
    }

    public async Task<bool> TestConnectionAsync(DatabaseType dbType, string host, int port, string database, string username, string password)
    {
        try
        {
            switch (dbType)
            {
                case DatabaseType.PostgreSQL:
                case DatabaseType.CockroachDB:
                case DatabaseType.Redshift:
                case DatabaseType.TimescaleDB:
                case DatabaseType.YugabyteDB:
                    return await TestPostgreSqlConnectionAsync(host, port, database, username, password);
                case DatabaseType.MySQL:
                case DatabaseType.MariaDB:
                    return await TestMySqlConnectionAsync(host, port, database, username, password);
                case DatabaseType.SQLServer:
                    return await TestSqlServerConnectionAsync(host, port, database, username, password);
                case DatabaseType.MongoDB:
                    return await TestMongoDbConnectionAsync(host, port, database, username, password);
                case DatabaseType.Oracle:
                    return await TestOracleConnectionAsync(host, port, database, username, password);
                case DatabaseType.SQLite:
                    return await TestSqliteConnectionAsync(host, port, database, username, password);
                case DatabaseType.ClickHouse:
                    return await TestClickHouseConnectionAsync(host, port, database, username, password);
                case DatabaseType.Firebird:
                    return await TestFirebirdConnectionAsync(host, port, database, username, password);
                case DatabaseType.DuckDB:
                    return await TestDuckDbConnectionAsync(host, port, database, username, password);
                case DatabaseType.Snowflake:
                    return await TestSnowflakeConnectionAsync(host, port, database, username, password);
                default:
                    throw new NotSupportedException($"Database type {dbType} is not supported");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test connection for {DbType}", dbType);
            return false;
        }
    }

    public async Task<string> GetSchemaAsync(DatabaseType dbType, string host, int port, string database, string username, string password)
    {
        try
        {
            return dbType switch
            {
                DatabaseType.PostgreSQL or DatabaseType.CockroachDB or DatabaseType.Redshift
                    or DatabaseType.TimescaleDB or DatabaseType.YugabyteDB
                    => await GetPostgreSqlSchemaAsync(host, port, database, username, password),
                DatabaseType.MySQL or DatabaseType.MariaDB
                    => await GetMySqlSchemaAsync(host, port, database, username, password),
                DatabaseType.SQLServer => await GetSqlServerSchemaAsync(host, port, database, username, password),
                DatabaseType.MongoDB => await GetMongoDbSchemaAsync(host, port, database, username, password),
                DatabaseType.Oracle => await GetOracleSchemaAsync(host, port, database, username, password),
                DatabaseType.SQLite => await GetSqliteSchemaAsync(host, port, database, username, password),
                DatabaseType.ClickHouse => await GetClickHouseSchemaAsync(host, port, database, username, password),
                DatabaseType.Firebird => await GetFirebirdSchemaAsync(host, port, database, username, password),
                DatabaseType.DuckDB => await GetDuckDbSchemaAsync(host, port, database, username, password),
                DatabaseType.Snowflake => await GetSnowflakeSchemaAsync(host, port, database, username, password),
                _ => throw new NotSupportedException($"Database type {dbType} is not supported")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get schema for {DbType}", dbType);
            throw;
        }
    }

    public async Task<List<TableSchema>> GetStructuredSchemaAsync(DatabaseType dbType, string host, int port, string database, string username, string password)
    {
        try
        {
            return dbType switch
            {
                DatabaseType.PostgreSQL or DatabaseType.CockroachDB or DatabaseType.Redshift
                    or DatabaseType.TimescaleDB or DatabaseType.YugabyteDB
                    => await GetPostgreSqlStructuredSchemaAsync(host, port, database, username, password),
                DatabaseType.MySQL or DatabaseType.MariaDB
                    => await GetMySqlStructuredSchemaAsync(host, port, database, username, password),
                DatabaseType.SQLServer => await GetSqlServerStructuredSchemaAsync(host, port, database, username, password),
                DatabaseType.MongoDB => await GetMongoDbStructuredSchemaAsync(host, port, database, username, password),
                DatabaseType.Oracle => await GetOracleStructuredSchemaAsync(host, port, database, username, password),
                DatabaseType.SQLite => await GetSqliteStructuredSchemaAsync(host, port, database, username, password),
                DatabaseType.ClickHouse => await GetClickHouseStructuredSchemaAsync(host, port, database, username, password),
                DatabaseType.Firebird => await GetFirebirdStructuredSchemaAsync(host, port, database, username, password),
                DatabaseType.DuckDB => await GetDuckDbStructuredSchemaAsync(host, port, database, username, password),
                DatabaseType.Snowflake => await GetSnowflakeStructuredSchemaAsync(host, port, database, username, password),
                _ => throw new NotSupportedException($"Database type {dbType} is not supported")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get structured schema for {DbType}", dbType);
            throw;
        }
    }

    public async Task<string> ExecuteQueryAsync(DatabaseType dbType, string host, int port, string database, string username, string password, string query)
    {
        try
        {
            return dbType switch
            {
                DatabaseType.PostgreSQL or DatabaseType.CockroachDB or DatabaseType.Redshift
                    or DatabaseType.TimescaleDB or DatabaseType.YugabyteDB
                    => await ExecutePostgreSqlQueryAsync(host, port, database, username, password, query),
                DatabaseType.MySQL or DatabaseType.MariaDB
                    => await ExecuteMySqlQueryAsync(host, port, database, username, password, query),
                DatabaseType.SQLServer => await ExecuteSqlServerQueryAsync(host, port, database, username, password, query),
                DatabaseType.MongoDB => await ExecuteMongoDbQueryAsync(host, port, database, username, password, query),
                DatabaseType.Oracle => await ExecuteOracleQueryAsync(host, port, database, username, password, query),
                DatabaseType.SQLite => await ExecuteSqliteQueryAsync(host, port, database, username, password, query),
                DatabaseType.ClickHouse => await ExecuteClickHouseQueryAsync(host, port, database, username, password, query),
                DatabaseType.Firebird => await ExecuteFirebirdQueryAsync(host, port, database, username, password, query),
                DatabaseType.DuckDB => await ExecuteDuckDbQueryAsync(host, port, database, username, password, query),
                DatabaseType.Snowflake => await ExecuteSnowflakeQueryAsync(host, port, database, username, password, query),
                _ => throw new NotSupportedException($"Database type {dbType} is not supported")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute query for {DbType}", dbType);
            throw;
        }
    }

    public async Task<List<string>> ExecuteQueriesAsync(DatabaseType dbType, string host, int port, string database, string username, string password, List<string> queries)
    {
        try
        {
            return dbType switch
            {
                DatabaseType.PostgreSQL or DatabaseType.CockroachDB or DatabaseType.Redshift
                    or DatabaseType.TimescaleDB or DatabaseType.YugabyteDB
                    => await ExecutePostgreSqlQueriesAsync(host, port, database, username, password, queries),
                DatabaseType.MySQL or DatabaseType.MariaDB
                    => await ExecuteMySqlQueriesAsync(host, port, database, username, password, queries),
                DatabaseType.SQLServer => await ExecuteSqlServerQueriesAsync(host, port, database, username, password, queries),
                DatabaseType.Oracle => await ExecuteOracleQueriesAsync(host, port, database, username, password, queries),
                DatabaseType.SQLite => await ExecuteSqliteQueriesAsync(host, port, database, username, password, queries),
                DatabaseType.ClickHouse => await ExecuteClickHouseQueriesAsync(host, port, database, username, password, queries),
                DatabaseType.Firebird => await ExecuteFirebirdQueriesAsync(host, port, database, username, password, queries),
                DatabaseType.DuckDB => await ExecuteDuckDbQueriesAsync(host, port, database, username, password, queries),
                DatabaseType.Snowflake => await ExecuteSnowflakeQueriesAsync(host, port, database, username, password, queries),
                // MongoDB doesn't support multiple SQL queries
                _ => throw new NotSupportedException($"Batch queries not supported for {dbType}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute batch queries for {DbType}", dbType);
            throw;
        }
    }

    #region PostgreSQL

    private static string BuildNpgsqlConnectionString(string host, int port, string database, string username, string password, bool readOnly = false)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            Password = password,
            SslMode = SslMode.Prefer,
            TrustServerCertificate = true,
            Timeout = 30
        };
        if (readOnly) builder.Options = "-c default_transaction_read_only=on";
        return builder.ConnectionString;
    }

    private async Task<bool> TestPostgreSqlConnectionAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new NpgsqlConnection(BuildNpgsqlConnectionString(host, port, database, username, password));
        await connection.OpenAsync();
        return true;
    }

    private async Task<string> GetPostgreSqlSchemaAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = BuildNpgsqlConnectionString(host, port, database, username, password);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var schema = new StringBuilder();

        // Single query: tables + row counts + all columns + all foreign keys
        // This replaces 3 separate queries (tables, FKs, N columns queries) with 1
        var allDataQuery = @"
            WITH table_info AS (
                SELECT c.relname AS table_name, c.reltuples::bigint AS approx_rows
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relkind = 'r'
            ),
            all_columns AS (
                SELECT table_name, column_name, data_type, is_nullable, ordinal_position
                FROM information_schema.columns
                WHERE table_schema = 'public'
            ),
            all_fks AS (
                SELECT
                    tc.table_name AS from_table,
                    kcu.column_name AS from_column,
                    ccu.table_name AS to_table,
                    ccu.column_name AS to_column
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                    ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
                JOIN information_schema.constraint_column_usage ccu
                    ON tc.constraint_name = ccu.constraint_name AND tc.table_schema = ccu.table_schema
                WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public'
            )
            SELECT 'T' AS row_type, t.table_name, t.approx_rows::text, NULL, NULL, NULL, NULL, 0
            FROM table_info t
            UNION ALL
            SELECT 'C', c.table_name, c.column_name, c.data_type, c.is_nullable, NULL, NULL, c.ordinal_position
            FROM all_columns c
            UNION ALL
            SELECT 'F', f.from_table, f.from_column, f.to_table, f.to_column, NULL, NULL, 0
            FROM all_fks f
            ORDER BY 2, 1, 8";

        var tables = new Dictionary<string, long>();
        var columns = new Dictionary<string, List<string>>();
        var foreignKeys = new Dictionary<string, List<string>>();

        await using var cmd = new NpgsqlCommand(allDataQuery, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var rowType = reader.GetString(0);
            var tableName = reader.GetString(1);

            switch (rowType)
            {
                case "T":
                    tables[tableName] = long.Parse(reader.GetString(2));
                    break;
                case "C":
                {
                    var colName = reader.GetString(2);
                    var dataType = reader.GetString(3);
                    var isNullable = reader.GetString(4) == "YES" ? "NULL" : "NOT NULL";
                    if (!columns.ContainsKey(tableName))
                        columns[tableName] = new List<string>();
                    columns[tableName].Add($"    \"{colName}\" {dataType} {isNullable}");
                    break;
                }
                case "F":
                {
                    var fromCol = reader.GetString(2);
                    var toTable = reader.GetString(3);
                    var toCol = reader.GetString(4);
                    if (!foreignKeys.ContainsKey(tableName))
                        foreignKeys[tableName] = new List<string>();
                    foreignKeys[tableName].Add($"    -- FK: {fromCol} → {toTable}.{toCol}");
                    break;
                }
            }
        }

        foreach (var (table, rows) in tables.OrderBy(t => t.Key))
        {
            schema.AppendLine($"-- {table} (~{rows:N0} rows)");
            schema.AppendLine($"CREATE TABLE \"{table}\" (");

            if (columns.TryGetValue(table, out var cols))
            {
                schema.AppendLine(string.Join(",\n", cols));
            }

            if (foreignKeys.TryGetValue(table, out var fks))
            {
                schema.AppendLine(string.Join("\n", fks));
            }

            schema.AppendLine(");");
            schema.AppendLine();
        }

        return schema.ToString();
    }

    private async Task<string> ExecutePostgreSqlQueryAsync(string host, int port, string database, string username, string password, string query)
    {
        ValidateReadOnlyQuery(query);
        var connectionString = BuildNpgsqlConnectionString(host, port, database, username, password, readOnly: true);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(query, connection);
        command.CommandTimeout = 60;
        await using var reader = await command.ExecuteReaderAsync();

        return await DataReaderToJsonAsync(reader);
    }

    private async Task<List<string>> ExecutePostgreSqlQueriesAsync(string host, int port, string database, string username, string password, List<string> queries)
    {
        var connectionString = BuildNpgsqlConnectionString(host, port, database, username, password, readOnly: true);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var results = new List<string>();
        foreach (var query in queries)
        {
            try
            {
                ValidateReadOnlyQuery(query);
                await using var command = new NpgsqlCommand(query, connection);
                command.CommandTimeout = 60;
                await using var reader = await command.ExecuteReaderAsync();
                results.Add(await DataReaderToJsonAsync(reader));
            }
            catch (Exception ex)
            {
                results.Add(JsonSerializer.Serialize(new { error = ex.Message, columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 }));
            }
        }
        return results;
    }

    private async Task<List<TableSchema>> GetPostgreSqlStructuredSchemaAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = BuildNpgsqlConnectionString(host, port, database, username, password);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var tables = new List<TableSchema>();

        // Get all tables
        var tableQuery = @"
            SELECT table_name, table_schema
            FROM information_schema.tables
            WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
            ORDER BY table_name";

        await using var tableCmd = new NpgsqlCommand(tableQuery, connection);
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tableList = new List<(string Name, string Schema)>();
        while (await tableReader.ReadAsync())
        {
            tableList.Add((tableReader.GetString(0), tableReader.GetString(1)));
        }
        await tableReader.CloseAsync();

        foreach (var (tableName, tableSchema) in tableList)
        {
            var table = new TableSchema
            {
                Name = tableName,
                Schema = tableSchema
            };

            // Get columns
            var columnQuery = @"
                SELECT
                    c.column_name,
                    c.data_type,
                    c.is_nullable,
                    c.column_default,
                    c.character_maximum_length,
                    c.numeric_precision,
                    c.numeric_scale,
                    CASE WHEN c.column_default LIKE 'nextval%' THEN true ELSE false END as is_identity
                FROM information_schema.columns c
                WHERE c.table_schema = @schema AND c.table_name = @tableName
                ORDER BY c.ordinal_position";

            await using var columnCmd = new NpgsqlCommand(columnQuery, connection);
            columnCmd.Parameters.AddWithValue("schema", tableSchema);
            columnCmd.Parameters.AddWithValue("tableName", tableName);
            await using var columnReader = await columnCmd.ExecuteReaderAsync();

            while (await columnReader.ReadAsync())
            {
                table.Columns.Add(new ColumnSchema
                {
                    Name = columnReader.GetString(0),
                    DataType = columnReader.GetString(1),
                    IsNullable = columnReader.GetString(2) == "YES",
                    DefaultValue = columnReader.IsDBNull(3) ? null : columnReader.GetString(3),
                    MaxLength = columnReader.IsDBNull(4) ? null : columnReader.GetInt32(4),
                    Precision = columnReader.IsDBNull(5) ? null : columnReader.GetInt32(5),
                    Scale = columnReader.IsDBNull(6) ? null : columnReader.GetInt32(6),
                    IsIdentity = columnReader.GetBoolean(7)
                });
            }
            await columnReader.CloseAsync();

            // Get primary keys
            var pkQuery = @"
                SELECT
                    tc.constraint_name,
                    kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                    ON tc.constraint_name = kcu.constraint_name
                    AND tc.table_schema = kcu.table_schema
                WHERE tc.constraint_type = 'PRIMARY KEY'
                    AND tc.table_schema = @schema
                    AND tc.table_name = @tableName
                ORDER BY kcu.ordinal_position";

            await using var pkCmd = new NpgsqlCommand(pkQuery, connection);
            pkCmd.Parameters.AddWithValue("schema", tableSchema);
            pkCmd.Parameters.AddWithValue("tableName", tableName);
            await using var pkReader = await pkCmd.ExecuteReaderAsync();

            var pkDict = new Dictionary<string, List<string>>();
            while (await pkReader.ReadAsync())
            {
                var pkName = pkReader.GetString(0);
                var columnName = pkReader.GetString(1);
                if (!pkDict.ContainsKey(pkName))
                    pkDict[pkName] = new List<string>();
                pkDict[pkName].Add(columnName);

                // Mark column as PK
                var col = table.Columns.FirstOrDefault(c => c.Name == columnName);
                if (col != null) col.IsPrimaryKey = true;
            }
            await pkReader.CloseAsync();

            foreach (var pk in pkDict)
            {
                table.PrimaryKeys.Add(new PrimaryKeyInfo { Name = pk.Key, Columns = pk.Value });
            }

            // Get foreign keys
            var fkQuery = @"
                SELECT
                    tc.constraint_name,
                    kcu.column_name,
                    ccu.table_name AS foreign_table_name,
                    ccu.column_name AS foreign_column_name,
                    rc.delete_rule,
                    rc.update_rule
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                    ON tc.constraint_name = kcu.constraint_name
                    AND tc.table_schema = kcu.table_schema
                JOIN information_schema.constraint_column_usage ccu
                    ON ccu.constraint_name = tc.constraint_name
                    AND ccu.table_schema = tc.table_schema
                JOIN information_schema.referential_constraints rc
                    ON tc.constraint_name = rc.constraint_name
                WHERE tc.constraint_type = 'FOREIGN KEY'
                    AND tc.table_schema = @schema
                    AND tc.table_name = @tableName";

            await using var fkCmd = new NpgsqlCommand(fkQuery, connection);
            fkCmd.Parameters.AddWithValue("schema", tableSchema);
            fkCmd.Parameters.AddWithValue("tableName", tableName);
            await using var fkReader = await fkCmd.ExecuteReaderAsync();

            while (await fkReader.ReadAsync())
            {
                var columnName = fkReader.GetString(1);
                table.ForeignKeys.Add(new ForeignKeyInfo
                {
                    Name = fkReader.GetString(0),
                    Column = columnName,
                    ReferencedTable = fkReader.GetString(2),
                    ReferencedColumn = fkReader.GetString(3),
                    OnDelete = fkReader.IsDBNull(4) ? null : fkReader.GetString(4),
                    OnUpdate = fkReader.IsDBNull(5) ? null : fkReader.GetString(5)
                });

                // Mark column as FK
                var col = table.Columns.FirstOrDefault(c => c.Name == columnName);
                if (col != null) col.IsForeignKey = true;
            }
            await fkReader.CloseAsync();

            // Get indexes
            var indexQuery = @"
                SELECT
                    i.relname AS index_name,
                    a.attname AS column_name,
                    ix.indisunique AS is_unique,
                    ix.indisclustered AS is_clustered
                FROM pg_class t
                JOIN pg_index ix ON t.oid = ix.indrelid
                JOIN pg_class i ON i.oid = ix.indexrelid
                JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey)
                JOIN pg_namespace n ON n.oid = t.relnamespace
                WHERE n.nspname = @schema
                    AND t.relname = @tableName
                    AND NOT ix.indisprimary
                ORDER BY i.relname, a.attnum";

            await using var indexCmd = new NpgsqlCommand(indexQuery, connection);
            indexCmd.Parameters.AddWithValue("schema", tableSchema);
            indexCmd.Parameters.AddWithValue("tableName", tableName);
            await using var indexReader = await indexCmd.ExecuteReaderAsync();

            var indexDict = new Dictionary<string, IndexInfo>();
            while (await indexReader.ReadAsync())
            {
                var indexName = indexReader.GetString(0);
                var columnName = indexReader.GetString(1);

                if (!indexDict.ContainsKey(indexName))
                {
                    indexDict[indexName] = new IndexInfo
                    {
                        Name = indexName,
                        IsUnique = indexReader.GetBoolean(2),
                        IsClustered = indexReader.GetBoolean(3),
                        Columns = new List<string>()
                    };
                }
                indexDict[indexName].Columns.Add(columnName);
            }
            await indexReader.CloseAsync();

            table.Indexes = indexDict.Values.ToList();

            // Get row count estimate
            var countQuery = @"
                SELECT reltuples::bigint AS estimate
                FROM pg_class
                WHERE relname = @tableName";

            await using var countCmd = new NpgsqlCommand(countQuery, connection);
            countCmd.Parameters.AddWithValue("tableName", tableName);
            var rowCount = await countCmd.ExecuteScalarAsync();
            table.RowCount = rowCount != null ? Convert.ToInt64(rowCount) : null;

            tables.Add(table);
        }

        return tables;
    }

    #endregion

    #region MySQL

    private static string BuildMySqlConnString(string host, int port, string database, string username, string password)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = (uint)port,
            Database = database,
            UserID = username,
            Password = password,
            SslMode = MySqlSslMode.Preferred,
            ConnectionTimeout = 30
        };
        return builder.ConnectionString;
    }

    private async Task<bool> TestMySqlConnectionAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = BuildMySqlConnString(host, port, database, username, password);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        return true;
    }

    private async Task<string> GetMySqlSchemaAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = BuildMySqlConnString(host, port, database, username, password);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        var schema = new StringBuilder();
        schema.AppendLine("-- MySQL Database Schema");
        schema.AppendLine();

        var tableQuery = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @database";
        await using var tableCmd = new MySqlCommand(tableQuery, connection);
        tableCmd.Parameters.AddWithValue("@database", database);
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tables = new List<string>();
        while (await tableReader.ReadAsync())
        {
            tables.Add(tableReader.GetString(0));
        }
        await tableReader.CloseAsync();

        foreach (var table in tables)
        {
            schema.AppendLine($"-- Table: {table}");
            var showCreateQuery = $"SHOW CREATE TABLE `{table}`";
            await using var createCmd = new MySqlCommand(showCreateQuery, connection);
            await using var createReader = await createCmd.ExecuteReaderAsync();
            if (await createReader.ReadAsync())
            {
                schema.AppendLine(createReader.GetString(1));
            }
            await createReader.CloseAsync();
            schema.AppendLine();
        }

        return schema.ToString();
    }

    private async Task<string> ExecuteMySqlQueryAsync(string host, int port, string database, string username, string password, string query)
    {
        ValidateReadOnlyQuery(query);
        var connectionString = BuildMySqlConnString(host, port, database, username, password);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        // Set session to read-only to prevent destructive queries
        await using (var roCmd = new MySqlCommand("SET SESSION TRANSACTION READ ONLY", connection))
        {
            await roCmd.ExecuteNonQueryAsync();
        }

        await using var command = new MySqlCommand(query, connection);
        command.CommandTimeout = 60;
        await using var reader = await command.ExecuteReaderAsync();

        return await DataReaderToJsonAsync(reader);
    }

    private async Task<List<string>> ExecuteMySqlQueriesAsync(string host, int port, string database, string username, string password, List<string> queries)
    {
        var connectionString = BuildMySqlConnString(host, port, database, username, password);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        // Set session to read-only to prevent destructive queries
        await using (var roCmd = new MySqlCommand("SET SESSION TRANSACTION READ ONLY", connection))
        {
            await roCmd.ExecuteNonQueryAsync();
        }

        var results = new List<string>();
        foreach (var query in queries)
        {
            try
            {
                ValidateReadOnlyQuery(query);
                await using var command = new MySqlCommand(query, connection);
                command.CommandTimeout = 60;
                await using var reader = await command.ExecuteReaderAsync();
                results.Add(await DataReaderToJsonAsync(reader));
            }
            catch (Exception ex)
            {
                results.Add(JsonSerializer.Serialize(new { error = ex.Message, columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 }));
            }
        }
        return results;
    }

    private async Task<List<TableSchema>> GetMySqlStructuredSchemaAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = BuildMySqlConnString(host, port, database, username, password);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        var tables = new List<TableSchema>();

        // Get all tables
        var tableQuery = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @database AND TABLE_TYPE = 'BASE TABLE'";
        await using var tableCmd = new MySqlCommand(tableQuery, connection);
        tableCmd.Parameters.AddWithValue("@database", database);
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tableNames = new List<string>();
        while (await tableReader.ReadAsync())
        {
            tableNames.Add(tableReader.GetString(0));
        }
        await tableReader.CloseAsync();

        foreach (var tableName in tableNames)
        {
            var table = new TableSchema { Name = tableName, Schema = database };

            // Get columns
            var columnQuery = @"
                SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT,
                       CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE,
                       EXTRA
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = @database AND TABLE_NAME = @tableName
                ORDER BY ORDINAL_POSITION";

            await using var columnCmd = new MySqlCommand(columnQuery, connection);
            columnCmd.Parameters.AddWithValue("@database", database);
            columnCmd.Parameters.AddWithValue("@tableName", tableName);
            await using var columnReader = await columnCmd.ExecuteReaderAsync();

            while (await columnReader.ReadAsync())
            {
                table.Columns.Add(new ColumnSchema
                {
                    Name = columnReader.GetString(0),
                    DataType = columnReader.GetString(1),
                    IsNullable = columnReader.GetString(2) == "YES",
                    DefaultValue = columnReader.IsDBNull(3) ? null : columnReader.GetString(3),
                    MaxLength = columnReader.IsDBNull(4) ? null : (int?)columnReader.GetInt64(4),
                    Precision = columnReader.IsDBNull(5) ? null : (int?)columnReader.GetInt64(5),
                    Scale = columnReader.IsDBNull(6) ? null : (int?)columnReader.GetInt64(6),
                    IsIdentity = !columnReader.IsDBNull(7) && columnReader.GetString(7).Contains("auto_increment")
                });
            }
            await columnReader.CloseAsync();

            // Get primary keys
            var pkQuery = @"
                SELECT CONSTRAINT_NAME, COLUMN_NAME
                FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
                WHERE TABLE_SCHEMA = @database AND TABLE_NAME = @tableName
                  AND CONSTRAINT_NAME = 'PRIMARY'
                ORDER BY ORDINAL_POSITION";

            await using var pkCmd = new MySqlCommand(pkQuery, connection);
            pkCmd.Parameters.AddWithValue("@database", database);
            pkCmd.Parameters.AddWithValue("@tableName", tableName);
            await using var pkReader = await pkCmd.ExecuteReaderAsync();

            var pkColumns = new List<string>();
            while (await pkReader.ReadAsync())
            {
                var colName = pkReader.GetString(1);
                pkColumns.Add(colName);
                var col = table.Columns.FirstOrDefault(c => c.Name == colName);
                if (col != null) col.IsPrimaryKey = true;
            }
            await pkReader.CloseAsync();

            if (pkColumns.Any())
            {
                table.PrimaryKeys.Add(new PrimaryKeyInfo { Name = "PRIMARY", Columns = pkColumns });
            }

            // Get foreign keys
            var fkQuery = @"
                SELECT
                    kcu.CONSTRAINT_NAME,
                    kcu.COLUMN_NAME,
                    kcu.REFERENCED_TABLE_NAME,
                    kcu.REFERENCED_COLUMN_NAME,
                    rc.DELETE_RULE,
                    rc.UPDATE_RULE
                FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                JOIN INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
                    ON kcu.CONSTRAINT_NAME = rc.CONSTRAINT_NAME
                    AND kcu.TABLE_SCHEMA = rc.CONSTRAINT_SCHEMA
                WHERE kcu.TABLE_SCHEMA = @database
                    AND kcu.TABLE_NAME = @tableName
                    AND kcu.REFERENCED_TABLE_NAME IS NOT NULL";

            await using var fkCmd = new MySqlCommand(fkQuery, connection);
            fkCmd.Parameters.AddWithValue("@database", database);
            fkCmd.Parameters.AddWithValue("@tableName", tableName);
            await using var fkReader = await fkCmd.ExecuteReaderAsync();

            while (await fkReader.ReadAsync())
            {
                var colName = fkReader.GetString(1);
                table.ForeignKeys.Add(new ForeignKeyInfo
                {
                    Name = fkReader.GetString(0),
                    Column = colName,
                    ReferencedTable = fkReader.GetString(2),
                    ReferencedColumn = fkReader.GetString(3),
                    OnDelete = fkReader.IsDBNull(4) ? null : fkReader.GetString(4),
                    OnUpdate = fkReader.IsDBNull(5) ? null : fkReader.GetString(5)
                });
                var col = table.Columns.FirstOrDefault(c => c.Name == colName);
                if (col != null) col.IsForeignKey = true;
            }
            await fkReader.CloseAsync();

            // Get indexes
            var indexQuery = @"
                SELECT INDEX_NAME, COLUMN_NAME, NON_UNIQUE
                FROM INFORMATION_SCHEMA.STATISTICS
                WHERE TABLE_SCHEMA = @database AND TABLE_NAME = @tableName
                  AND INDEX_NAME != 'PRIMARY'
                ORDER BY INDEX_NAME, SEQ_IN_INDEX";

            await using var indexCmd = new MySqlCommand(indexQuery, connection);
            indexCmd.Parameters.AddWithValue("@database", database);
            indexCmd.Parameters.AddWithValue("@tableName", tableName);
            await using var indexReader = await indexCmd.ExecuteReaderAsync();

            var indexDict = new Dictionary<string, IndexInfo>();
            while (await indexReader.ReadAsync())
            {
                var indexName = indexReader.GetString(0);
                var columnName = indexReader.GetString(1);
                var nonUnique = indexReader.GetInt32(2);

                if (!indexDict.ContainsKey(indexName))
                {
                    indexDict[indexName] = new IndexInfo
                    {
                        Name = indexName,
                        IsUnique = nonUnique == 0,
                        Columns = new List<string>()
                    };
                }
                indexDict[indexName].Columns.Add(columnName);
            }
            await indexReader.CloseAsync();

            table.Indexes = indexDict.Values.ToList();

            // Get row count
            var countQuery = "SELECT TABLE_ROWS FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @database AND TABLE_NAME = @tableName";
            await using var countCmd = new MySqlCommand(countQuery, connection);
            countCmd.Parameters.AddWithValue("@database", database);
            countCmd.Parameters.AddWithValue("@tableName", tableName);
            var rowCount = await countCmd.ExecuteScalarAsync();
            table.RowCount = rowCount != null && rowCount != DBNull.Value ? Convert.ToInt64(rowCount) : null;

            tables.Add(table);
        }

        return tables;
    }

    #endregion

    #region SQL Server

    private static string BuildSqlServerConnString(string host, int port, string database, string username, string password)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            InitialCatalog = database,
            UserID = username,
            Password = password,
            TrustServerCertificate = true,
            ConnectTimeout = 30
        };
        return builder.ConnectionString;
    }

    private async Task<bool> TestSqlServerConnectionAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = BuildSqlServerConnString(host, port, database, username, password);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return true;
    }

    private async Task<string> GetSqlServerSchemaAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = BuildSqlServerConnString(host, port, database, username, password);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var schema = new StringBuilder();
        schema.AppendLine("-- SQL Server Database Schema");
        schema.AppendLine();

        var tableQuery = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'";
        await using var tableCmd = new SqlCommand(tableQuery, connection);
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tables = new List<string>();
        while (await tableReader.ReadAsync())
        {
            tables.Add(tableReader.GetString(0));
        }
        await tableReader.CloseAsync();

        foreach (var table in tables)
        {
            schema.AppendLine($"-- Table: {table}");
            schema.AppendLine($"CREATE TABLE [{table}] (");

            var columnQuery = @"
                SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = @tableName
                ORDER BY ORDINAL_POSITION";

            await using var columnCmd = new SqlCommand(columnQuery, connection);
            columnCmd.Parameters.AddWithValue("@tableName", table);
            await using var columnReader = await columnCmd.ExecuteReaderAsync();

            var columns = new List<string>();
            while (await columnReader.ReadAsync())
            {
                var columnName = columnReader.GetString(0);
                var dataType = columnReader.GetString(1);
                var isNullable = columnReader.GetString(2) == "YES" ? "NULL" : "NOT NULL";
                columns.Add($"    [{columnName}] {dataType} {isNullable}");
            }
            await columnReader.CloseAsync();

            schema.AppendLine(string.Join(",\n", columns));
            schema.AppendLine(");");
            schema.AppendLine();
        }

        return schema.ToString();
    }

    private async Task<string> ExecuteSqlServerQueryAsync(string host, int port, string database, string username, string password, string query)
    {
        ValidateReadOnlyQuery(query);
        var connectionString = BuildSqlServerConnString(host, port, database, username, password);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // Wrap in a transaction that is always rolled back to prevent any writes
        var transaction = connection.BeginTransaction();
        try
        {
            await using var command = new SqlCommand(query, connection, transaction);
            command.CommandTimeout = 60;
            await using var reader = await command.ExecuteReaderAsync();
            return await DataReaderToJsonAsync(reader);
        }
        finally
        {
            try { transaction.Rollback(); } catch { /* already closed */ }
        }
    }

    private async Task<List<string>> ExecuteSqlServerQueriesAsync(string host, int port, string database, string username, string password, List<string> queries)
    {
        var connectionString = BuildSqlServerConnString(host, port, database, username, password);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var results = new List<string>();
        foreach (var query in queries)
        {
            var transaction = connection.BeginTransaction();
            try
            {
                ValidateReadOnlyQuery(query);
                await using var command = new SqlCommand(query, connection, transaction);
                command.CommandTimeout = 60;
                await using var reader = await command.ExecuteReaderAsync();
                results.Add(await DataReaderToJsonAsync(reader));
            }
            catch (Exception ex)
            {
                results.Add(JsonSerializer.Serialize(new { error = ex.Message, columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 }));
            }
            finally
            {
                try { transaction.Rollback(); } catch { /* already closed */ }
            }
        }
        return results;
    }

    private async Task<List<TableSchema>> GetSqlServerStructuredSchemaAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = BuildSqlServerConnString(host, port, database, username, password);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var tables = new List<TableSchema>();

        // Get all tables
        var tableQuery = "SELECT TABLE_NAME, TABLE_SCHEMA FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'";
        await using var tableCmd = new SqlCommand(tableQuery, connection);
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tableList = new List<(string Name, string Schema)>();
        while (await tableReader.ReadAsync())
        {
            tableList.Add((tableReader.GetString(0), tableReader.GetString(1)));
        }
        await tableReader.CloseAsync();

        foreach (var (tableName, tableSchema) in tableList)
        {
            var table = new TableSchema { Name = tableName, Schema = tableSchema };

            // Get columns
            var columnQuery = @"
                SELECT
                    c.COLUMN_NAME,
                    c.DATA_TYPE,
                    c.IS_NULLABLE,
                    c.COLUMN_DEFAULT,
                    c.CHARACTER_MAXIMUM_LENGTH,
                    c.NUMERIC_PRECISION,
                    c.NUMERIC_SCALE,
                    COLUMNPROPERTY(OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME), c.COLUMN_NAME, 'IsIdentity') as IsIdentity
                FROM INFORMATION_SCHEMA.COLUMNS c
                WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @tableName
                ORDER BY c.ORDINAL_POSITION";

            await using var columnCmd = new SqlCommand(columnQuery, connection);
            columnCmd.Parameters.AddWithValue("@schema", tableSchema);
            columnCmd.Parameters.AddWithValue("@tableName", tableName);
            await using var columnReader = await columnCmd.ExecuteReaderAsync();

            while (await columnReader.ReadAsync())
            {
                table.Columns.Add(new ColumnSchema
                {
                    Name = columnReader.GetString(0),
                    DataType = columnReader.GetString(1),
                    IsNullable = columnReader.GetString(2) == "YES",
                    DefaultValue = columnReader.IsDBNull(3) ? null : columnReader.GetString(3),
                    MaxLength = columnReader.IsDBNull(4) ? null : columnReader.GetInt32(4),
                    Precision = columnReader.IsDBNull(5) ? null : (int?)columnReader.GetByte(5),
                    Scale = columnReader.IsDBNull(6) ? null : columnReader.GetInt32(6),
                    IsIdentity = !columnReader.IsDBNull(7) && columnReader.GetInt32(7) == 1
                });
            }
            await columnReader.CloseAsync();

            // Get primary keys
            var pkQuery = @"
                SELECT
                    kc.CONSTRAINT_NAME,
                    kcu.COLUMN_NAME
                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                    ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
                    AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kc
                    ON tc.CONSTRAINT_NAME = kc.CONSTRAINT_NAME
                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                    AND tc.TABLE_SCHEMA = @schema
                    AND tc.TABLE_NAME = @tableName
                ORDER BY kcu.ORDINAL_POSITION";

            await using var pkCmd = new SqlCommand(pkQuery, connection);
            pkCmd.Parameters.AddWithValue("@schema", tableSchema);
            pkCmd.Parameters.AddWithValue("@tableName", tableName);
            await using var pkReader = await pkCmd.ExecuteReaderAsync();

            var pkDict = new Dictionary<string, List<string>>();
            while (await pkReader.ReadAsync())
            {
                var pkName = pkReader.GetString(0);
                var colName = pkReader.GetString(1);
                if (!pkDict.ContainsKey(pkName))
                    pkDict[pkName] = new List<string>();
                if (!pkDict[pkName].Contains(colName))
                    pkDict[pkName].Add(colName);

                var col = table.Columns.FirstOrDefault(c => c.Name == colName);
                if (col != null) col.IsPrimaryKey = true;
            }
            await pkReader.CloseAsync();

            foreach (var pk in pkDict)
            {
                table.PrimaryKeys.Add(new PrimaryKeyInfo { Name = pk.Key, Columns = pk.Value });
            }

            // Get foreign keys
            var fkQuery = @"
                SELECT
                    fk.name AS FK_NAME,
                    COL_NAME(fkc.parent_object_id, fkc.parent_column_id) AS FK_COLUMN,
                    OBJECT_NAME(fkc.referenced_object_id) AS REFERENCED_TABLE,
                    COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) AS REFERENCED_COLUMN,
                    fk.delete_referential_action_desc,
                    fk.update_referential_action_desc
                FROM sys.foreign_keys fk
                JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
                WHERE OBJECT_NAME(fk.parent_object_id) = @tableName
                  AND SCHEMA_NAME(fk.schema_id) = @schema";

            await using var fkCmd = new SqlCommand(fkQuery, connection);
            fkCmd.Parameters.AddWithValue("@schema", tableSchema);
            fkCmd.Parameters.AddWithValue("@tableName", tableName);
            await using var fkReader = await fkCmd.ExecuteReaderAsync();

            while (await fkReader.ReadAsync())
            {
                var colName = fkReader.GetString(1);
                table.ForeignKeys.Add(new ForeignKeyInfo
                {
                    Name = fkReader.GetString(0),
                    Column = colName,
                    ReferencedTable = fkReader.GetString(2),
                    ReferencedColumn = fkReader.GetString(3),
                    OnDelete = fkReader.IsDBNull(4) ? null : fkReader.GetString(4),
                    OnUpdate = fkReader.IsDBNull(5) ? null : fkReader.GetString(5)
                });
                var col = table.Columns.FirstOrDefault(c => c.Name == colName);
                if (col != null) col.IsForeignKey = true;
            }
            await fkReader.CloseAsync();

            // Get indexes
            var indexQuery = @"
                SELECT
                    i.name AS INDEX_NAME,
                    COL_NAME(ic.object_id, ic.column_id) AS COLUMN_NAME,
                    i.is_unique,
                    i.type_desc
                FROM sys.indexes i
                JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                WHERE OBJECT_NAME(i.object_id) = @tableName
                  AND OBJECT_SCHEMA_NAME(i.object_id) = @schema
                  AND i.is_primary_key = 0
                  AND i.name IS NOT NULL
                ORDER BY i.name, ic.key_ordinal";

            await using var indexCmd = new SqlCommand(indexQuery, connection);
            indexCmd.Parameters.AddWithValue("@schema", tableSchema);
            indexCmd.Parameters.AddWithValue("@tableName", tableName);
            await using var indexReader = await indexCmd.ExecuteReaderAsync();

            var indexDict = new Dictionary<string, IndexInfo>();
            while (await indexReader.ReadAsync())
            {
                var indexName = indexReader.GetString(0);
                var columnName = indexReader.GetString(1);

                if (!indexDict.ContainsKey(indexName))
                {
                    indexDict[indexName] = new IndexInfo
                    {
                        Name = indexName,
                        IsUnique = indexReader.GetBoolean(2),
                        IsClustered = indexReader.GetString(3) == "CLUSTERED",
                        Columns = new List<string>()
                    };
                }
                indexDict[indexName].Columns.Add(columnName);
            }
            await indexReader.CloseAsync();

            table.Indexes = indexDict.Values.ToList();

            // Get row count
            var countQuery = @"
                SELECT SUM(p.rows)
                FROM sys.partitions p
                JOIN sys.tables t ON p.object_id = t.object_id
                WHERE t.name = @tableName
                  AND SCHEMA_NAME(t.schema_id) = @schema
                  AND p.index_id IN (0, 1)";

            await using var countCmd = new SqlCommand(countQuery, connection);
            countCmd.Parameters.AddWithValue("@schema", tableSchema);
            countCmd.Parameters.AddWithValue("@tableName", tableName);
            var rowCount = await countCmd.ExecuteScalarAsync();
            table.RowCount = rowCount != null && rowCount != DBNull.Value ? Convert.ToInt64(rowCount) : null;

            tables.Add(table);
        }

        return tables;
    }

    #endregion

    #region MongoDB

    private static string BuildMongoDbConnectionString(string host, int port, string username, string password)
    {
        if (string.IsNullOrEmpty(username))
            return $"mongodb://{host}:{port}";

        // URL-encode username and password to handle special characters safely
        var encodedUsername = Uri.EscapeDataString(username);
        var encodedPassword = Uri.EscapeDataString(password);
        return $"mongodb://{encodedUsername}:{encodedPassword}@{host}:{port}";
    }

    private async Task<bool> TestMongoDbConnectionAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = BuildMongoDbConnectionString(host, port, username, password);

        var client = new MongoClient(connectionString);
        var db = client.GetDatabase(database);
        await db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        return true;
    }

    private async Task<string> GetMongoDbSchemaAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = BuildMongoDbConnectionString(host, port, username, password);

        var client = new MongoClient(connectionString);
        var db = client.GetDatabase(database);

        var schema = new StringBuilder();
        schema.AppendLine("-- MongoDB Database Schema (Sample Documents)");
        schema.AppendLine();

        var collections = await db.ListCollectionNamesAsync();
        var collectionList = await collections.ToListAsync();

        foreach (var collectionName in collectionList)
        {
            schema.AppendLine($"-- Collection: {collectionName}");

            var collection = db.GetCollection<BsonDocument>(collectionName);
            var sampleDoc = await collection.Find(new BsonDocument()).FirstOrDefaultAsync();

            if (sampleDoc != null)
            {
                schema.AppendLine("Sample document structure:");
                schema.AppendLine(sampleDoc.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { Indent = true }));
            }
            else
            {
                schema.AppendLine("(empty collection)");
            }
            schema.AppendLine();
        }

        return schema.ToString();
    }

    private async Task<string> ExecuteMongoDbQueryAsync(string host, int port, string database, string username, string password, string query)
    {
        var connectionString = BuildMongoDbConnectionString(host, port, username, password);

        var client = new MongoClient(connectionString);
        var db = client.GetDatabase(database);

        // Parse the query (expected format: collectionName.find({...}) or similar)
        // For simplicity, we'll use RunCommandAsync
        var command = BsonDocument.Parse(query);

        // Validate that the command is read-only
        ValidateMongoDbCommand(command);

        var result = await db.RunCommandAsync<BsonDocument>(command);

        return result.ToJson();
    }

    private async Task<List<TableSchema>> GetMongoDbStructuredSchemaAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = BuildMongoDbConnectionString(host, port, username, password);

        var client = new MongoClient(connectionString);
        var db = client.GetDatabase(database);

        var tables = new List<TableSchema>();

        var collections = await db.ListCollectionNamesAsync();
        var collectionList = await collections.ToListAsync();

        foreach (var collectionName in collectionList)
        {
            var table = new TableSchema
            {
                Name = collectionName,
                Schema = database
            };

            var collection = db.GetCollection<BsonDocument>(collectionName);

            // Get sample documents to infer schema
            var sampleDocs = await collection.Find(new BsonDocument()).Limit(100).ToListAsync();

            if (sampleDocs.Any())
            {
                // Infer columns from all sample documents
                var fieldTypes = new Dictionary<string, HashSet<string>>();

                foreach (var doc in sampleDocs)
                {
                    foreach (var element in doc.Elements)
                    {
                        if (!fieldTypes.ContainsKey(element.Name))
                            fieldTypes[element.Name] = new HashSet<string>();
                        fieldTypes[element.Name].Add(element.Value.BsonType.ToString());
                    }
                }

                foreach (var field in fieldTypes)
                {
                    table.Columns.Add(new ColumnSchema
                    {
                        Name = field.Key,
                        DataType = string.Join(" | ", field.Value),
                        IsNullable = true,
                        IsPrimaryKey = field.Key == "_id"
                    });
                }

                // MongoDB _id is always the primary key
                if (table.Columns.Any(c => c.Name == "_id"))
                {
                    table.PrimaryKeys.Add(new PrimaryKeyInfo
                    {
                        Name = "_id",
                        Columns = new List<string> { "_id" }
                    });
                }
            }

            // Get indexes
            var indexCursor = await collection.Indexes.ListAsync();
            var indexes = await indexCursor.ToListAsync();

            foreach (var index in indexes)
            {
                var indexName = index.GetValue("name").AsString;
                if (indexName == "_id_") continue; // Skip default _id index

                var keys = index.GetValue("key").AsBsonDocument;
                var indexInfo = new IndexInfo
                {
                    Name = indexName,
                    IsUnique = index.Contains("unique") && index.GetValue("unique").AsBoolean,
                    Columns = keys.Elements.Select(e => e.Name).ToList()
                };
                table.Indexes.Add(indexInfo);
            }

            // Get estimated document count
            table.RowCount = await collection.EstimatedDocumentCountAsync();

            tables.Add(table);
        }

        return tables;
    }

    #endregion

    #region Oracle

    private static string BuildOracleConnectionString(string host, int port, string database, string username, string password)
    {
        // Sanitize values by removing semicolons and parentheses to prevent connection string injection
        static string Sanitize(string value) => value?.Replace(";", "").Replace("(", "").Replace(")", "") ?? "";
        return $"Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={Sanitize(host)})(PORT={port}))(CONNECT_DATA=(SERVICE_NAME={Sanitize(database)})));User Id={Sanitize(username)};Password={Sanitize(password)};Connection Timeout=30";
    }

    private async Task<bool> TestOracleConnectionAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new OracleConnection(BuildOracleConnectionString(host, port, database, username, password));
        await connection.OpenAsync();
        return true;
    }

    private async Task<string> GetOracleSchemaAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new OracleConnection(BuildOracleConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        var schema = new StringBuilder();
        schema.AppendLine("-- Oracle Database Schema");
        schema.AppendLine();

        await using var tableCmd = new OracleCommand("SELECT table_name FROM user_tables ORDER BY table_name", connection);
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tables = new List<string>();
        while (await tableReader.ReadAsync()) tables.Add(tableReader.GetString(0));
        await tableReader.CloseAsync();

        foreach (var table in tables)
        {
            schema.AppendLine($"CREATE TABLE \"{table}\" (");
            await using var colCmd = new OracleCommand(
                "SELECT column_name, data_type, nullable FROM user_tab_columns WHERE table_name = :tableName ORDER BY column_id", connection);
            colCmd.Parameters.Add(new OracleParameter("tableName", table));
            await using var colReader = await colCmd.ExecuteReaderAsync();

            var cols = new List<string>();
            while (await colReader.ReadAsync())
            {
                var colName = colReader.GetString(0);
                var dataType = colReader.GetString(1);
                var nullable = colReader.GetString(2) == "Y" ? "NULL" : "NOT NULL";
                cols.Add($"    \"{colName}\" {dataType} {nullable}");
            }
            await colReader.CloseAsync();

            schema.AppendLine(string.Join(",\n", cols));
            schema.AppendLine(");");
            schema.AppendLine();
        }

        return schema.ToString();
    }

    private async Task<List<TableSchema>> GetOracleStructuredSchemaAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new OracleConnection(BuildOracleConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        var tables = new List<TableSchema>();

        await using var tableCmd = new OracleCommand("SELECT table_name FROM user_tables ORDER BY table_name", connection);
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tableNames = new List<string>();
        while (await tableReader.ReadAsync()) tableNames.Add(tableReader.GetString(0));
        await tableReader.CloseAsync();

        foreach (var tableName in tableNames)
        {
            var table = new TableSchema { Name = tableName, Schema = username.ToUpperInvariant() };

            // Columns
            await using var colCmd = new OracleCommand(
                "SELECT column_name, data_type, nullable, data_default, char_length, data_precision, data_scale FROM user_tab_columns WHERE table_name = :tableName ORDER BY column_id", connection);
            colCmd.Parameters.Add(new OracleParameter("tableName", tableName));
            await using var colReader = await colCmd.ExecuteReaderAsync();
            while (await colReader.ReadAsync())
            {
                table.Columns.Add(new ColumnSchema
                {
                    Name = colReader.GetString(0),
                    DataType = colReader.GetString(1),
                    IsNullable = colReader.GetString(2) == "Y",
                    DefaultValue = colReader.IsDBNull(3) ? null : colReader.GetString(3),
                    MaxLength = colReader.IsDBNull(4) ? null : colReader.GetInt32(4),
                    Precision = colReader.IsDBNull(5) ? null : colReader.GetInt32(5),
                    Scale = colReader.IsDBNull(6) ? null : colReader.GetInt32(6)
                });
            }
            await colReader.CloseAsync();

            // Primary keys
            await using var pkCmd = new OracleCommand(
                "SELECT cols.constraint_name, cols.column_name FROM user_cons_columns cols JOIN user_constraints cons ON cols.constraint_name = cons.constraint_name WHERE cons.constraint_type = 'P' AND cons.table_name = :tableName ORDER BY cols.position", connection);
            pkCmd.Parameters.Add(new OracleParameter("tableName", tableName));
            await using var pkReader = await pkCmd.ExecuteReaderAsync();
            var pkDict = new Dictionary<string, List<string>>();
            while (await pkReader.ReadAsync())
            {
                var pkName = pkReader.GetString(0);
                var colName = pkReader.GetString(1);
                if (!pkDict.ContainsKey(pkName)) pkDict[pkName] = new List<string>();
                pkDict[pkName].Add(colName);
                var col = table.Columns.FirstOrDefault(c => c.Name == colName);
                if (col != null) col.IsPrimaryKey = true;
            }
            await pkReader.CloseAsync();
            foreach (var pk in pkDict)
                table.PrimaryKeys.Add(new PrimaryKeyInfo { Name = pk.Key, Columns = pk.Value });

            // Foreign keys
            await using var fkCmd = new OracleCommand(
                @"SELECT a.constraint_name, a.column_name, c_pk.table_name, b.column_name
                   FROM user_cons_columns a
                   JOIN user_constraints c ON a.constraint_name = c.constraint_name
                   JOIN user_constraints c_pk ON c.r_constraint_name = c_pk.constraint_name
                   JOIN user_cons_columns b ON c_pk.constraint_name = b.constraint_name AND a.position = b.position
                   WHERE c.constraint_type = 'R' AND c.table_name = :tableName", connection);
            fkCmd.Parameters.Add(new OracleParameter("tableName", tableName));
            await using var fkReader = await fkCmd.ExecuteReaderAsync();
            while (await fkReader.ReadAsync())
            {
                var colName = fkReader.GetString(1);
                table.ForeignKeys.Add(new ForeignKeyInfo
                {
                    Name = fkReader.GetString(0),
                    Column = colName,
                    ReferencedTable = fkReader.GetString(2),
                    ReferencedColumn = fkReader.GetString(3)
                });
                var col = table.Columns.FirstOrDefault(c => c.Name == colName);
                if (col != null) col.IsForeignKey = true;
            }
            await fkReader.CloseAsync();

            // Row count
            await using var countCmd = new OracleCommand("SELECT num_rows FROM user_tables WHERE table_name = :tableName", connection);
            countCmd.Parameters.Add(new OracleParameter("tableName", tableName));
            var rowCount = await countCmd.ExecuteScalarAsync();
            table.RowCount = rowCount != null && rowCount != DBNull.Value ? Convert.ToInt64(rowCount) : null;

            tables.Add(table);
        }

        return tables;
    }

    private async Task<string> ExecuteOracleQueryAsync(string host, int port, string database, string username, string password, string query)
    {
        ValidateReadOnlyQuery(query);

        await using var connection = new OracleConnection(BuildOracleConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        // Set transaction to read-only to prevent destructive queries
        await using (var roCmd = new OracleCommand("SET TRANSACTION READ ONLY", connection))
        {
            await roCmd.ExecuteNonQueryAsync();
        }

        await using var command = new OracleCommand(query, connection);
        command.CommandTimeout = 60;
        await using var reader = await command.ExecuteReaderAsync();
        return await DataReaderToJsonAsync(reader);
    }

    private async Task<List<string>> ExecuteOracleQueriesAsync(string host, int port, string database, string username, string password, List<string> queries)
    {
        await using var connection = new OracleConnection(BuildOracleConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        // Set transaction to read-only to prevent destructive queries
        await using (var roCmd = new OracleCommand("SET TRANSACTION READ ONLY", connection))
        {
            await roCmd.ExecuteNonQueryAsync();
        }

        var results = new List<string>();
        foreach (var query in queries)
        {
            try
            {
                ValidateReadOnlyQuery(query);

                await using var command = new OracleCommand(query, connection);
                command.CommandTimeout = 60;
                await using var reader = await command.ExecuteReaderAsync();
                results.Add(await DataReaderToJsonAsync(reader));
            }
            catch (Exception ex)
            {
                results.Add(JsonSerializer.Serialize(new { error = ex.Message, columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 }));
            }
        }
        return results;
    }

    #endregion

    #region SQLite

    private static string BuildSqliteConnectionString(string host, int port, string database, string username, string password, bool readOnly = false)
    {
        // For SQLite, host is treated as the file path. If password is provided, use it.
        var builder = new SqliteConnectionStringBuilder { DataSource = string.IsNullOrEmpty(host) ? database : host };
        if (!string.IsNullOrEmpty(password)) builder.Password = password;
        if (readOnly) builder.Mode = SqliteOpenMode.ReadOnly;
        return builder.ConnectionString;
    }

    private async Task<bool> TestSqliteConnectionAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new SqliteConnection(BuildSqliteConnectionString(host, port, database, username, password));
        await connection.OpenAsync();
        return true;
    }

    private async Task<string> GetSqliteSchemaAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new SqliteConnection(BuildSqliteConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        var schema = new StringBuilder();
        schema.AppendLine("-- SQLite Database Schema");
        schema.AppendLine();

        await using var tableCmd = new SqliteCommand("SELECT name, sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name", connection);
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        while (await tableReader.ReadAsync())
        {
            var tableName = tableReader.GetString(0);
            var createSql = tableReader.IsDBNull(1) ? "" : tableReader.GetString(1);
            schema.AppendLine($"-- Table: {tableName}");
            schema.AppendLine(createSql + ";");
            schema.AppendLine();
        }

        return schema.ToString();
    }

    private async Task<List<TableSchema>> GetSqliteStructuredSchemaAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new SqliteConnection(BuildSqliteConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        var tables = new List<TableSchema>();

        await using var tableCmd = new SqliteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name", connection);
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tableNames = new List<string>();
        while (await tableReader.ReadAsync()) tableNames.Add(tableReader.GetString(0));
        await tableReader.CloseAsync();

        foreach (var tableName in tableNames)
        {
            var table = new TableSchema { Name = tableName, Schema = "main" };

            await using var colCmd = new SqliteCommand($"PRAGMA table_info(\"{tableName}\")", connection);
            await using var colReader = await colCmd.ExecuteReaderAsync();
            while (await colReader.ReadAsync())
            {
                var colName = colReader.GetString(1);
                var isPk = colReader.GetInt32(5) > 0;
                table.Columns.Add(new ColumnSchema
                {
                    Name = colName,
                    DataType = colReader.GetString(2),
                    IsNullable = colReader.GetInt32(3) == 0,
                    DefaultValue = colReader.IsDBNull(4) ? null : colReader.GetString(4),
                    IsPrimaryKey = isPk
                });
                if (isPk)
                {
                    table.PrimaryKeys.Add(new PrimaryKeyInfo { Name = "pk_" + colName, Columns = new List<string> { colName } });
                }
            }
            await colReader.CloseAsync();

            // Foreign keys
            await using var fkCmd = new SqliteCommand($"PRAGMA foreign_key_list(\"{tableName}\")", connection);
            await using var fkReader = await fkCmd.ExecuteReaderAsync();
            while (await fkReader.ReadAsync())
            {
                var colName = fkReader.GetString(3);
                table.ForeignKeys.Add(new ForeignKeyInfo
                {
                    Name = $"fk_{tableName}_{colName}",
                    Column = colName,
                    ReferencedTable = fkReader.GetString(2),
                    ReferencedColumn = fkReader.GetString(4)
                });
                var col = table.Columns.FirstOrDefault(c => c.Name == colName);
                if (col != null) col.IsForeignKey = true;
            }
            await fkReader.CloseAsync();

            // Row count
            await using var countCmd = new SqliteCommand($"SELECT COUNT(*) FROM \"{tableName}\"", connection);
            var rowCount = await countCmd.ExecuteScalarAsync();
            table.RowCount = rowCount != null ? Convert.ToInt64(rowCount) : null;

            tables.Add(table);
        }

        return tables;
    }

    private async Task<string> ExecuteSqliteQueryAsync(string host, int port, string database, string username, string password, string query)
    {
        ValidateReadOnlyQuery(query);

        await using var connection = new SqliteConnection(BuildSqliteConnectionString(host, port, database, username, password, readOnly: true));
        await connection.OpenAsync();
        await using var command = new SqliteCommand(query, connection);
        command.CommandTimeout = 60;
        await using var reader = await command.ExecuteReaderAsync();
        return await DataReaderToJsonAsync(reader);
    }

    private async Task<List<string>> ExecuteSqliteQueriesAsync(string host, int port, string database, string username, string password, List<string> queries)
    {
        await using var connection = new SqliteConnection(BuildSqliteConnectionString(host, port, database, username, password, readOnly: true));
        await connection.OpenAsync();

        var results = new List<string>();
        foreach (var query in queries)
        {
            try
            {
                ValidateReadOnlyQuery(query);

                await using var command = new SqliteCommand(query, connection);
                command.CommandTimeout = 60;
                await using var reader = await command.ExecuteReaderAsync();
                results.Add(await DataReaderToJsonAsync(reader));
            }
            catch (Exception ex)
            {
                results.Add(JsonSerializer.Serialize(new { error = ex.Message, columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 }));
            }
        }
        return results;
    }

    #endregion

    #region ClickHouse

    private static string BuildClickHouseConnectionString(string host, int port, string database, string username, string password)
    {
        // Sanitize values by removing semicolons to prevent connection string injection
        static string Sanitize(string value) => value?.Replace(";", "") ?? "";
        return $"Host={Sanitize(host)};Port={port};Database={Sanitize(database)};Username={Sanitize(username)};Password={Sanitize(password)}";
    }

    private async Task<bool> TestClickHouseConnectionAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new ClickHouseConnection(BuildClickHouseConnectionString(host, port, database, username, password));
        await connection.OpenAsync();
        return true;
    }

    private async Task<string> GetClickHouseSchemaAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new ClickHouseConnection(BuildClickHouseConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        var safeDatabase = ValidateIdentifier(database);

        var schema = new StringBuilder();
        schema.AppendLine("-- ClickHouse Database Schema");
        schema.AppendLine();

        await using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = $"SELECT name FROM system.tables WHERE database = '{safeDatabase}' ORDER BY name";
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tableNames = new List<string>();
        while (await tableReader.ReadAsync()) tableNames.Add(tableReader.GetString(0));
        await tableReader.CloseAsync();

        foreach (var tableName in tableNames)
        {
            var safeTableName = ValidateIdentifier(tableName);
            schema.AppendLine($"CREATE TABLE \"{safeTableName}\" (");
            await using var colCmd = connection.CreateCommand();
            colCmd.CommandText = $"SELECT name, type FROM system.columns WHERE database = '{safeDatabase}' AND table = '{safeTableName}' ORDER BY position";
            await using var colReader = await colCmd.ExecuteReaderAsync();

            var cols = new List<string>();
            while (await colReader.ReadAsync())
                cols.Add($"    \"{colReader.GetString(0)}\" {colReader.GetString(1)}");
            await colReader.CloseAsync();

            schema.AppendLine(string.Join(",\n", cols));
            schema.AppendLine(");");
            schema.AppendLine();
        }

        return schema.ToString();
    }

    private async Task<List<TableSchema>> GetClickHouseStructuredSchemaAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new ClickHouseConnection(BuildClickHouseConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        var safeDatabase = ValidateIdentifier(database);
        var tables = new List<TableSchema>();

        await using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = $"SELECT name FROM system.tables WHERE database = '{safeDatabase}' ORDER BY name";
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tableNames = new List<string>();
        while (await tableReader.ReadAsync()) tableNames.Add(tableReader.GetString(0));
        await tableReader.CloseAsync();

        foreach (var tableName in tableNames)
        {
            var safeTableName = ValidateIdentifier(tableName);
            var table = new TableSchema { Name = tableName, Schema = database };

            await using var colCmd = connection.CreateCommand();
            colCmd.CommandText = $"SELECT name, type, is_in_primary_key FROM system.columns WHERE database = '{safeDatabase}' AND table = '{safeTableName}' ORDER BY position";
            await using var colReader = await colCmd.ExecuteReaderAsync();
            while (await colReader.ReadAsync())
            {
                var typeStr = colReader.GetString(1);
                var isPk = colReader.GetFieldValue<byte>(2) == 1;
                var colName = colReader.GetString(0);
                table.Columns.Add(new ColumnSchema
                {
                    Name = colName,
                    DataType = typeStr,
                    IsNullable = typeStr.StartsWith("Nullable("),
                    IsPrimaryKey = isPk
                });
                if (isPk)
                    table.PrimaryKeys.Add(new PrimaryKeyInfo { Name = "pk_" + colName, Columns = new List<string> { colName } });
            }
            await colReader.CloseAsync();

            // Row count
            await using var countCmd = connection.CreateCommand();
            countCmd.CommandText = $"SELECT count() FROM \"{safeDatabase}\".\"{safeTableName}\"";
            var rowCount = await countCmd.ExecuteScalarAsync();
            table.RowCount = rowCount != null ? Convert.ToInt64(rowCount) : null;

            tables.Add(table);
        }

        return tables;
    }

    private async Task<string> ExecuteClickHouseQueryAsync(string host, int port, string database, string username, string password, string query)
    {
        ValidateReadOnlyQuery(query);

        await using var connection = new ClickHouseConnection(BuildClickHouseConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        // Set ClickHouse to read-only mode
        await using (var roCmd = connection.CreateCommand())
        {
            roCmd.CommandText = "SET readonly = 1";
            await roCmd.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = 60;
        await using var reader = await command.ExecuteReaderAsync();
        return await DataReaderToJsonAsync(reader);
    }

    private async Task<List<string>> ExecuteClickHouseQueriesAsync(string host, int port, string database, string username, string password, List<string> queries)
    {
        await using var connection = new ClickHouseConnection(BuildClickHouseConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        // Set ClickHouse to read-only mode
        await using (var roCmd = connection.CreateCommand())
        {
            roCmd.CommandText = "SET readonly = 1";
            await roCmd.ExecuteNonQueryAsync();
        }

        var results = new List<string>();
        foreach (var query in queries)
        {
            try
            {
                ValidateReadOnlyQuery(query);

                await using var command = connection.CreateCommand();
                command.CommandText = query;
                command.CommandTimeout = 60;
                await using var reader = await command.ExecuteReaderAsync();
                results.Add(await DataReaderToJsonAsync(reader));
            }
            catch (Exception ex)
            {
                results.Add(JsonSerializer.Serialize(new { error = ex.Message, columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 }));
            }
        }
        return results;
    }

    #endregion

    #region Firebird

    private static string BuildFirebirdConnectionString(string host, int port, string database, string username, string password)
    {
        // Sanitize values by removing semicolons to prevent connection string injection
        static string Sanitize(string value) => value?.Replace(";", "") ?? "";
        return $"Server={Sanitize(host)};Port={port};Database={Sanitize(database)};User={Sanitize(username)};Password={Sanitize(password)};Connection Timeout=30";
    }

    private async Task<bool> TestFirebirdConnectionAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new FbConnection(BuildFirebirdConnectionString(host, port, database, username, password));
        await connection.OpenAsync();
        return true;
    }

    private async Task<string> GetFirebirdSchemaAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new FbConnection(BuildFirebirdConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        var schema = new StringBuilder();
        schema.AppendLine("-- Firebird Database Schema");
        schema.AppendLine();

        await using var tableCmd = new FbCommand(
            "SELECT RDB$RELATION_NAME FROM RDB$RELATIONS WHERE RDB$SYSTEM_FLAG = 0 AND RDB$VIEW_BLR IS NULL ORDER BY RDB$RELATION_NAME", connection);
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tableNames = new List<string>();
        while (await tableReader.ReadAsync()) tableNames.Add(tableReader.GetString(0).Trim());
        await tableReader.CloseAsync();

        foreach (var tableName in tableNames)
        {
            schema.AppendLine($"CREATE TABLE \"{tableName}\" (");
            await using var colCmd = new FbCommand(
                @"SELECT rf.RDB$FIELD_NAME, f.RDB$FIELD_TYPE, rf.RDB$NULL_FLAG
                   FROM RDB$RELATION_FIELDS rf
                   JOIN RDB$FIELDS f ON rf.RDB$FIELD_SOURCE = f.RDB$FIELD_NAME
                   WHERE rf.RDB$RELATION_NAME = @tableName
                   ORDER BY rf.RDB$FIELD_POSITION", connection);
            colCmd.Parameters.AddWithValue("@tableName", tableName);
            await using var colReader = await colCmd.ExecuteReaderAsync();

            var cols = new List<string>();
            while (await colReader.ReadAsync())
            {
                var colName = colReader.GetString(0).Trim();
                var dataType = MapFirebirdType(colReader.GetInt16(1));
                var nullable = colReader.IsDBNull(2) ? "NULL" : "NOT NULL";
                cols.Add($"    \"{colName}\" {dataType} {nullable}");
            }
            await colReader.CloseAsync();

            schema.AppendLine(string.Join(",\n", cols));
            schema.AppendLine(");");
            schema.AppendLine();
        }

        return schema.ToString();
    }

    private static string MapFirebirdType(short typeCode) => typeCode switch
    {
        7 => "SMALLINT", 8 => "INTEGER", 10 => "FLOAT", 12 => "DATE", 13 => "TIME",
        14 => "CHAR", 16 => "BIGINT", 27 => "DOUBLE PRECISION", 35 => "TIMESTAMP",
        37 => "VARCHAR", 261 => "BLOB", _ => $"TYPE_{typeCode}"
    };

    private async Task<List<TableSchema>> GetFirebirdStructuredSchemaAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new FbConnection(BuildFirebirdConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        var tables = new List<TableSchema>();

        await using var tableCmd = new FbCommand(
            "SELECT RDB$RELATION_NAME FROM RDB$RELATIONS WHERE RDB$SYSTEM_FLAG = 0 AND RDB$VIEW_BLR IS NULL ORDER BY RDB$RELATION_NAME", connection);
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tableNames = new List<string>();
        while (await tableReader.ReadAsync()) tableNames.Add(tableReader.GetString(0).Trim());
        await tableReader.CloseAsync();

        foreach (var tableName in tableNames)
        {
            var table = new TableSchema { Name = tableName, Schema = "DEFAULT" };

            await using var colCmd = new FbCommand(
                @"SELECT rf.RDB$FIELD_NAME, f.RDB$FIELD_TYPE, rf.RDB$NULL_FLAG, f.RDB$FIELD_LENGTH, f.RDB$FIELD_PRECISION, f.RDB$FIELD_SCALE
                   FROM RDB$RELATION_FIELDS rf
                   JOIN RDB$FIELDS f ON rf.RDB$FIELD_SOURCE = f.RDB$FIELD_NAME
                   WHERE rf.RDB$RELATION_NAME = @tableName
                   ORDER BY rf.RDB$FIELD_POSITION", connection);
            colCmd.Parameters.AddWithValue("@tableName", tableName);
            await using var colReader = await colCmd.ExecuteReaderAsync();
            while (await colReader.ReadAsync())
            {
                table.Columns.Add(new ColumnSchema
                {
                    Name = colReader.GetString(0).Trim(),
                    DataType = MapFirebirdType(colReader.GetInt16(1)),
                    IsNullable = colReader.IsDBNull(2),
                    MaxLength = colReader.IsDBNull(3) ? null : colReader.GetInt32(3),
                    Precision = colReader.IsDBNull(4) ? null : colReader.GetInt32(4),
                    Scale = colReader.IsDBNull(5) ? null : Math.Abs(colReader.GetInt32(5))
                });
            }
            await colReader.CloseAsync();

            tables.Add(table);
        }

        return tables;
    }

    private async Task<string> ExecuteFirebirdQueryAsync(string host, int port, string database, string username, string password, string query)
    {
        ValidateReadOnlyQuery(query);

        await using var connection = new FbConnection(BuildFirebirdConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        // Use snapshot transaction (read-only) to prevent writes
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.Snapshot);
        try
        {
            await using var command = new FbCommand(query, connection, transaction);
            command.CommandTimeout = 60;
            await using var reader = await command.ExecuteReaderAsync();
            var result = await DataReaderToJsonAsync(reader);
            return result;
        }
        finally
        {
            try { await transaction.RollbackAsync(); } catch { /* connection may already be closed */ }
        }
    }

    private async Task<List<string>> ExecuteFirebirdQueriesAsync(string host, int port, string database, string username, string password, List<string> queries)
    {
        await using var connection = new FbConnection(BuildFirebirdConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        var results = new List<string>();
        foreach (var query in queries)
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.Snapshot);
            try
            {
                ValidateReadOnlyQuery(query);

                await using var command = new FbCommand(query, connection, transaction);
                command.CommandTimeout = 60;
                await using var reader = await command.ExecuteReaderAsync();
                results.Add(await DataReaderToJsonAsync(reader));
            }
            catch (Exception ex)
            {
                results.Add(JsonSerializer.Serialize(new { error = ex.Message, columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 }));
            }
            finally
            {
                try { await transaction.RollbackAsync(); } catch { /* connection may already be closed */ }
            }
        }
        return results;
    }

    #endregion

    #region DuckDB

    private static string BuildDuckDbConnectionString(string host, int port, string database, string username, string password)
    {
        // DuckDB uses file path as the data source. Host is treated as file path.
        var path = string.IsNullOrEmpty(host) ? database : host;
        // Sanitize by removing semicolons to prevent connection string injection
        return $"Data Source={path?.Replace(";", "") ?? ""}";
    }

    private async Task<bool> TestDuckDbConnectionAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new DuckDBConnection(BuildDuckDbConnectionString(host, port, database, username, password));
        await connection.OpenAsync();
        return true;
    }

    private async Task<string> GetDuckDbSchemaAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new DuckDBConnection(BuildDuckDbConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        var schema = new StringBuilder();
        schema.AppendLine("-- DuckDB Database Schema");
        schema.AppendLine();

        await using var tableCmd = new DuckDBCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = 'main' ORDER BY table_name", connection);
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tableNames = new List<string>();
        while (await tableReader.ReadAsync()) tableNames.Add(tableReader.GetString(0));
        await tableReader.CloseAsync();

        foreach (var tableName in tableNames)
        {
            var safeTableName = ValidateIdentifier(tableName);
            schema.AppendLine($"CREATE TABLE \"{safeTableName}\" (");
            await using var colCmd = new DuckDBCommand(
                $"SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema = 'main' AND table_name = '{safeTableName}' ORDER BY ordinal_position", connection);
            await using var colReader = await colCmd.ExecuteReaderAsync();

            var cols = new List<string>();
            while (await colReader.ReadAsync())
            {
                var colName = colReader.GetString(0);
                var dataType = colReader.GetString(1);
                var nullable = colReader.GetString(2) == "YES" ? "NULL" : "NOT NULL";
                cols.Add($"    \"{colName}\" {dataType} {nullable}");
            }
            await colReader.CloseAsync();

            schema.AppendLine(string.Join(",\n", cols));
            schema.AppendLine(");");
            schema.AppendLine();
        }

        return schema.ToString();
    }

    private async Task<List<TableSchema>> GetDuckDbStructuredSchemaAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new DuckDBConnection(BuildDuckDbConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        var tables = new List<TableSchema>();

        await using var tableCmd = new DuckDBCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = 'main' ORDER BY table_name", connection);
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tableNames = new List<string>();
        while (await tableReader.ReadAsync()) tableNames.Add(tableReader.GetString(0));
        await tableReader.CloseAsync();

        foreach (var tableName in tableNames)
        {
            var safeTableName = ValidateIdentifier(tableName);
            var table = new TableSchema { Name = tableName, Schema = "main" };

            await using var colCmd = new DuckDBCommand(
                $"SELECT column_name, data_type, is_nullable, column_default, numeric_precision, numeric_scale FROM information_schema.columns WHERE table_schema = 'main' AND table_name = '{safeTableName}' ORDER BY ordinal_position", connection);
            await using var colReader = await colCmd.ExecuteReaderAsync();
            while (await colReader.ReadAsync())
            {
                table.Columns.Add(new ColumnSchema
                {
                    Name = colReader.GetString(0),
                    DataType = colReader.GetString(1),
                    IsNullable = colReader.GetString(2) == "YES",
                    DefaultValue = colReader.IsDBNull(3) ? null : colReader.GetString(3),
                    Precision = colReader.IsDBNull(4) ? null : colReader.GetInt32(4),
                    Scale = colReader.IsDBNull(5) ? null : colReader.GetInt32(5)
                });
            }
            await colReader.CloseAsync();

            // Row count
            await using var countCmd = new DuckDBCommand($"SELECT COUNT(*) FROM \"{safeTableName}\"", connection);
            var rowCount = await countCmd.ExecuteScalarAsync();
            table.RowCount = rowCount != null ? Convert.ToInt64(rowCount) : null;

            tables.Add(table);
        }

        return tables;
    }

    private async Task<string> ExecuteDuckDbQueryAsync(string host, int port, string database, string username, string password, string query)
    {
        ValidateReadOnlyQuery(query);

        await using var connection = new DuckDBConnection(BuildDuckDbConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        // Set DuckDB to read-only access mode
        await using (var roCmd = new DuckDBCommand("SET access_mode = 'read_only'", connection))
        {
            await roCmd.ExecuteNonQueryAsync();
        }

        await using var command = new DuckDBCommand(query, connection);
        command.CommandTimeout = 60;
        await using var reader = await command.ExecuteReaderAsync();
        return await DataReaderToJsonAsync(reader);
    }

    private async Task<List<string>> ExecuteDuckDbQueriesAsync(string host, int port, string database, string username, string password, List<string> queries)
    {
        await using var connection = new DuckDBConnection(BuildDuckDbConnectionString(host, port, database, username, password));
        await connection.OpenAsync();

        // Set DuckDB to read-only access mode
        await using (var roCmd = new DuckDBCommand("SET access_mode = 'read_only'", connection))
        {
            await roCmd.ExecuteNonQueryAsync();
        }

        var results = new List<string>();
        foreach (var query in queries)
        {
            try
            {
                ValidateReadOnlyQuery(query);

                await using var command = new DuckDBCommand(query, connection);
                command.CommandTimeout = 60;
                await using var reader = await command.ExecuteReaderAsync();
                results.Add(await DataReaderToJsonAsync(reader));
            }
            catch (Exception ex)
            {
                results.Add(JsonSerializer.Serialize(new { error = ex.Message, columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 }));
            }
        }
        return results;
    }

    #endregion

    #region Snowflake

    private static string BuildSnowflakeConnectionString(string host, int port, string database, string username, string password)
    {
        // Sanitize values by removing semicolons to prevent connection string injection
        static string Sanitize(string value) => value?.Replace(";", "") ?? "";
        return $"account={Sanitize(host)};user={Sanitize(username)};password={Sanitize(password)};db={Sanitize(database)};scheme=https;port={port}";
    }

    private async Task<bool> TestSnowflakeConnectionAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new SnowflakeDbConnection { ConnectionString = BuildSnowflakeConnectionString(host, port, database, username, password) };
        await connection.OpenAsync();
        return true;
    }

    private async Task<string> GetSnowflakeSchemaAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new SnowflakeDbConnection { ConnectionString = BuildSnowflakeConnectionString(host, port, database, username, password) };
        await connection.OpenAsync();

        var schema = new StringBuilder();
        schema.AppendLine("-- Snowflake Database Schema");
        schema.AppendLine();

        var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = 'PUBLIC' AND table_type = 'BASE TABLE' ORDER BY table_name";
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tableNames = new List<string>();
        while (await tableReader.ReadAsync()) tableNames.Add(tableReader.GetString(0));
        await tableReader.CloseAsync();

        foreach (var tableName in tableNames)
        {
            var safeTableName = ValidateIdentifier(tableName);
            schema.AppendLine($"CREATE TABLE \"{safeTableName}\" (");
            var colCmd = connection.CreateCommand();
            colCmd.CommandText = $"SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema = 'PUBLIC' AND table_name = '{safeTableName}' ORDER BY ordinal_position";
            await using var colReader = await colCmd.ExecuteReaderAsync();

            var cols = new List<string>();
            while (await colReader.ReadAsync())
            {
                var colName = colReader.GetString(0);
                var dataType = colReader.GetString(1);
                var nullable = colReader.GetString(2) == "YES" ? "NULL" : "NOT NULL";
                cols.Add($"    \"{colName}\" {dataType} {nullable}");
            }
            await colReader.CloseAsync();

            schema.AppendLine(string.Join(",\n", cols));
            schema.AppendLine(");");
            schema.AppendLine();
        }

        return schema.ToString();
    }

    private async Task<List<TableSchema>> GetSnowflakeStructuredSchemaAsync(string host, int port, string database, string username, string password)
    {
        await using var connection = new SnowflakeDbConnection { ConnectionString = BuildSnowflakeConnectionString(host, port, database, username, password) };
        await connection.OpenAsync();

        var tables = new List<TableSchema>();

        var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = 'PUBLIC' AND table_type = 'BASE TABLE' ORDER BY table_name";
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tableNames = new List<string>();
        while (await tableReader.ReadAsync()) tableNames.Add(tableReader.GetString(0));
        await tableReader.CloseAsync();

        foreach (var tableName in tableNames)
        {
            var safeTableName = ValidateIdentifier(tableName);
            var table = new TableSchema { Name = tableName, Schema = "PUBLIC" };

            var colCmd = connection.CreateCommand();
            colCmd.CommandText = $"SELECT column_name, data_type, is_nullable, column_default, numeric_precision, numeric_scale FROM information_schema.columns WHERE table_schema = 'PUBLIC' AND table_name = '{safeTableName}' ORDER BY ordinal_position";
            await using var colReader = await colCmd.ExecuteReaderAsync();
            while (await colReader.ReadAsync())
            {
                table.Columns.Add(new ColumnSchema
                {
                    Name = colReader.GetString(0),
                    DataType = colReader.GetString(1),
                    IsNullable = colReader.GetString(2) == "YES",
                    DefaultValue = colReader.IsDBNull(3) ? null : colReader.GetString(3),
                    Precision = colReader.IsDBNull(4) ? null : colReader.GetInt32(4),
                    Scale = colReader.IsDBNull(5) ? null : colReader.GetInt32(5)
                });
            }
            await colReader.CloseAsync();

            // Row count
            var countCmd = connection.CreateCommand();
            countCmd.CommandText = $"SELECT row_count FROM information_schema.tables WHERE table_schema = 'PUBLIC' AND table_name = '{safeTableName}'";
            var rowCount = await countCmd.ExecuteScalarAsync();
            table.RowCount = rowCount != null && rowCount != DBNull.Value ? Convert.ToInt64(rowCount) : null;

            tables.Add(table);
        }

        return tables;
    }

    private async Task<string> ExecuteSnowflakeQueryAsync(string host, int port, string database, string username, string password, string query)
    {
        ValidateReadOnlyQuery(query);

        await using var connection = new SnowflakeDbConnection { ConnectionString = BuildSnowflakeConnectionString(host, port, database, username, password) };
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = 60;
        await using var reader = await command.ExecuteReaderAsync();
        return await DataReaderToJsonAsync(reader);
    }

    private async Task<List<string>> ExecuteSnowflakeQueriesAsync(string host, int port, string database, string username, string password, List<string> queries)
    {
        await using var connection = new SnowflakeDbConnection { ConnectionString = BuildSnowflakeConnectionString(host, port, database, username, password) };
        await connection.OpenAsync();

        var results = new List<string>();
        foreach (var query in queries)
        {
            try
            {
                ValidateReadOnlyQuery(query);

                var command = connection.CreateCommand();
                command.CommandText = query;
                command.CommandTimeout = 60;
                await using var reader = await command.ExecuteReaderAsync();
                results.Add(await DataReaderToJsonAsync(reader));
            }
            catch (Exception ex)
            {
                results.Add(JsonSerializer.Serialize(new { error = ex.Message, columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 }));
            }
        }
        return results;
    }

    #endregion

    #region Helpers

    private async Task<string> DataReaderToJsonAsync(IDataReader reader)
    {
        const int maxResultRows = 100000; // Support up to 100k rows
        const int maxCellLength = 5000; // Truncate individual cell values longer than this
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var columns = new List<string>();
        var truncated = false;
        var rowCount = 0;

        // Get column names
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(reader.GetName(i));
        }

        // Use streaming JSON writer to avoid building huge object in memory
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = true });

        writer.WriteStartObject();

        // Write columns
        writer.WritePropertyName("columns");
        writer.WriteStartArray();
        foreach (var col in columns)
        {
            writer.WriteStringValue(col);
        }
        writer.WriteEndArray();

        // Write rows array - streaming
        writer.WritePropertyName("rows");
        writer.WriteStartArray();

        var dbReader = reader as System.Data.Common.DbDataReader;

        while (dbReader != null ? await dbReader.ReadAsync() : await Task.Run(() => reader.Read()))
        {
            if (rowCount >= maxResultRows)
            {
                truncated = true;
                break;
            }

            writer.WriteStartObject();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var colName = reader.GetName(i);
                writer.WritePropertyName(colName);

                if (reader.IsDBNull(i))
                {
                    writer.WriteNullValue();
                }
                else
                {
                    var value = reader.GetValue(i);
                    WriteJsonValue(writer, value, maxCellLength);
                }
            }
            writer.WriteEndObject();
            rowCount++;

            // Flush periodically to avoid huge buffers
            if (rowCount % 10000 == 0)
            {
                await writer.FlushAsync();
            }
        }

        writer.WriteEndArray();

        stopwatch.Stop();

        // Write metadata
        writer.WriteNumber("rowCount", rowCount);
        writer.WriteNumber("executionTimeMs", (int)stopwatch.ElapsedMilliseconds);

        if (truncated)
        {
            writer.WriteBoolean("truncated", true);
            writer.WriteNumber("maxRows", maxResultRows);
        }

        writer.WriteEndObject();
        await writer.FlushAsync();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object value, int maxCellLength)
    {
        switch (value)
        {
            case string strVal:
                if (strVal.Length > maxCellLength)
                    writer.WriteStringValue(strVal.Substring(0, maxCellLength) + "...");
                else
                    writer.WriteStringValue(strVal);
                break;
            case bool boolVal:
                writer.WriteBooleanValue(boolVal);
                break;
            case int intVal:
                writer.WriteNumberValue(intVal);
                break;
            case long longVal:
                writer.WriteNumberValue(longVal);
                break;
            case decimal decVal:
                writer.WriteNumberValue(decVal);
                break;
            case double doubleVal:
                if (double.IsNaN(doubleVal) || double.IsInfinity(doubleVal))
                    writer.WriteNullValue();
                else
                    writer.WriteNumberValue(doubleVal);
                break;
            case float floatVal:
                if (float.IsNaN(floatVal) || float.IsInfinity(floatVal))
                    writer.WriteNullValue();
                else
                    writer.WriteNumberValue(floatVal);
                break;
            case DateTime dateVal:
                writer.WriteStringValue(dateVal.ToString("O"));
                break;
            case DateTimeOffset dtoVal:
                writer.WriteStringValue(dtoVal.ToString("O"));
                break;
            case Guid guidVal:
                writer.WriteStringValue(guidVal.ToString());
                break;
            case byte[] byteVal:
                writer.WriteStringValue(byteVal.Length > 100 ? $"[Binary: {byteVal.Length} bytes]" : Convert.ToBase64String(byteVal));
                break;
            default:
                var strValue = value?.ToString() ?? "";
                if (strValue.Length > maxCellLength)
                    writer.WriteStringValue(strValue.Substring(0, maxCellLength) + "...");
                else
                    writer.WriteStringValue(strValue);
                break;
        }
    }

    #endregion
}
