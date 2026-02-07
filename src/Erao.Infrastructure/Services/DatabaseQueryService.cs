using System.Data;
using System.Text;
using System.Text.Json;
using Erao.Core.DTOs.Database;
using Erao.Core.Enums;
using Erao.Core.Interfaces;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MySql.Data.MySqlClient;
using Npgsql;
using System.Data.SqlClient;

namespace Erao.Infrastructure.Services;

public class DatabaseQueryService : IDatabaseQueryService
{
    private readonly ILogger<DatabaseQueryService> _logger;

    public DatabaseQueryService(ILogger<DatabaseQueryService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> TestConnectionAsync(DatabaseType dbType, string host, int port, string database, string username, string password)
    {
        try
        {
            switch (dbType)
            {
                case DatabaseType.PostgreSQL:
                    return await TestPostgreSqlConnectionAsync(host, port, database, username, password);
                case DatabaseType.MySQL:
                    return await TestMySqlConnectionAsync(host, port, database, username, password);
                case DatabaseType.SQLServer:
                    return await TestSqlServerConnectionAsync(host, port, database, username, password);
                case DatabaseType.MongoDB:
                    return await TestMongoDbConnectionAsync(host, port, database, username, password);
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
                DatabaseType.PostgreSQL => await GetPostgreSqlSchemaAsync(host, port, database, username, password),
                DatabaseType.MySQL => await GetMySqlSchemaAsync(host, port, database, username, password),
                DatabaseType.SQLServer => await GetSqlServerSchemaAsync(host, port, database, username, password),
                DatabaseType.MongoDB => await GetMongoDbSchemaAsync(host, port, database, username, password),
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
                DatabaseType.PostgreSQL => await GetPostgreSqlStructuredSchemaAsync(host, port, database, username, password),
                DatabaseType.MySQL => await GetMySqlStructuredSchemaAsync(host, port, database, username, password),
                DatabaseType.SQLServer => await GetSqlServerStructuredSchemaAsync(host, port, database, username, password),
                DatabaseType.MongoDB => await GetMongoDbStructuredSchemaAsync(host, port, database, username, password),
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
                DatabaseType.PostgreSQL => await ExecutePostgreSqlQueryAsync(host, port, database, username, password, query),
                DatabaseType.MySQL => await ExecuteMySqlQueryAsync(host, port, database, username, password, query),
                DatabaseType.SQLServer => await ExecuteSqlServerQueryAsync(host, port, database, username, password, query),
                DatabaseType.MongoDB => await ExecuteMongoDbQueryAsync(host, port, database, username, password, query),
                _ => throw new NotSupportedException($"Database type {dbType} is not supported")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute query for {DbType}", dbType);
            throw;
        }
    }

    #region PostgreSQL

    private async Task<bool> TestPostgreSqlConnectionAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = $"Host={host};Port={port};Database={database};Username={username};Password={password};Timeout=30";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return true;
    }

    private async Task<string> GetPostgreSqlSchemaAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = $"Host={host};Port={port};Database={database};Username={username};Password={password}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var schema = new StringBuilder();
        schema.AppendLine("-- PostgreSQL Database Schema");
        schema.AppendLine();

        var tableQuery = @"
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public'
            ORDER BY table_name";

        await using var tableCmd = new NpgsqlCommand(tableQuery, connection);
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
            schema.AppendLine($"CREATE TABLE {table} (");

            var columnQuery = @"
                SELECT column_name, data_type, is_nullable, column_default
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @tableName
                ORDER BY ordinal_position";

            await using var columnCmd = new NpgsqlCommand(columnQuery, connection);
            columnCmd.Parameters.AddWithValue("tableName", table);
            await using var columnReader = await columnCmd.ExecuteReaderAsync();

            var columns = new List<string>();
            while (await columnReader.ReadAsync())
            {
                var columnName = columnReader.GetString(0);
                var dataType = columnReader.GetString(1);
                var isNullable = columnReader.GetString(2) == "YES" ? "NULL" : "NOT NULL";
                columns.Add($"    {columnName} {dataType} {isNullable}");
            }
            await columnReader.CloseAsync();

            schema.AppendLine(string.Join(",\n", columns));
            schema.AppendLine(");");
            schema.AppendLine();
        }

        return schema.ToString();
    }

    private async Task<string> ExecutePostgreSqlQueryAsync(string host, int port, string database, string username, string password, string query)
    {
        var connectionString = $"Host={host};Port={port};Database={database};Username={username};Password={password}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();

        return await DataReaderToJsonAsync(reader);
    }

    private async Task<List<TableSchema>> GetPostgreSqlStructuredSchemaAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = $"Host={host};Port={port};Database={database};Username={username};Password={password}";
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

    private async Task<bool> TestMySqlConnectionAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = $"Server={host};Port={port};Database={database};User={username};Password={password};Connection Timeout=30";
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        return true;
    }

    private async Task<string> GetMySqlSchemaAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = $"Server={host};Port={port};Database={database};User={username};Password={password}";
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        var schema = new StringBuilder();
        schema.AppendLine("-- MySQL Database Schema");
        schema.AppendLine();

        var tableQuery = $"SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{database}'";
        await using var tableCmd = new MySqlCommand(tableQuery, connection);
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
        var connectionString = $"Server={host};Port={port};Database={database};User={username};Password={password}";
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new MySqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();

        return await DataReaderToJsonAsync(reader);
    }

    private async Task<List<TableSchema>> GetMySqlStructuredSchemaAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = $"Server={host};Port={port};Database={database};User={username};Password={password}";
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        var tables = new List<TableSchema>();

        // Get all tables
        var tableQuery = $"SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{database}' AND TABLE_TYPE = 'BASE TABLE'";
        await using var tableCmd = new MySqlCommand(tableQuery, connection);
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
            var columnQuery = $@"
                SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT,
                       CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE,
                       EXTRA
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = '{database}' AND TABLE_NAME = '{tableName}'
                ORDER BY ORDINAL_POSITION";

            await using var columnCmd = new MySqlCommand(columnQuery, connection);
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
            var pkQuery = $@"
                SELECT CONSTRAINT_NAME, COLUMN_NAME
                FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
                WHERE TABLE_SCHEMA = '{database}' AND TABLE_NAME = '{tableName}'
                  AND CONSTRAINT_NAME = 'PRIMARY'
                ORDER BY ORDINAL_POSITION";

            await using var pkCmd = new MySqlCommand(pkQuery, connection);
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
            var fkQuery = $@"
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
                WHERE kcu.TABLE_SCHEMA = '{database}'
                    AND kcu.TABLE_NAME = '{tableName}'
                    AND kcu.REFERENCED_TABLE_NAME IS NOT NULL";

            await using var fkCmd = new MySqlCommand(fkQuery, connection);
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
            var indexQuery = $@"
                SELECT INDEX_NAME, COLUMN_NAME, NON_UNIQUE
                FROM INFORMATION_SCHEMA.STATISTICS
                WHERE TABLE_SCHEMA = '{database}' AND TABLE_NAME = '{tableName}'
                  AND INDEX_NAME != 'PRIMARY'
                ORDER BY INDEX_NAME, SEQ_IN_INDEX";

            await using var indexCmd = new MySqlCommand(indexQuery, connection);
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
            var countQuery = $"SELECT TABLE_ROWS FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{database}' AND TABLE_NAME = '{tableName}'";
            await using var countCmd = new MySqlCommand(countQuery, connection);
            var rowCount = await countCmd.ExecuteScalarAsync();
            table.RowCount = rowCount != null && rowCount != DBNull.Value ? Convert.ToInt64(rowCount) : null;

            tables.Add(table);
        }

        return tables;
    }

    #endregion

    #region SQL Server

    private async Task<bool> TestSqlServerConnectionAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = $"Server={host},{port};Database={database};User Id={username};Password={password};TrustServerCertificate=True;Connection Timeout=30";
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return true;
    }

    private async Task<string> GetSqlServerSchemaAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = $"Server={host},{port};Database={database};User Id={username};Password={password};TrustServerCertificate=True";
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
        var connectionString = $"Server={host},{port};Database={database};User Id={username};Password={password};TrustServerCertificate=True";
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();

        return await DataReaderToJsonAsync(reader);
    }

    private async Task<List<TableSchema>> GetSqlServerStructuredSchemaAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = $"Server={host},{port};Database={database};User Id={username};Password={password};TrustServerCertificate=True";
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

    private async Task<bool> TestMongoDbConnectionAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = string.IsNullOrEmpty(username)
            ? $"mongodb://{host}:{port}"
            : $"mongodb://{username}:{password}@{host}:{port}";

        var client = new MongoClient(connectionString);
        var db = client.GetDatabase(database);
        await db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        return true;
    }

    private async Task<string> GetMongoDbSchemaAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = string.IsNullOrEmpty(username)
            ? $"mongodb://{host}:{port}"
            : $"mongodb://{username}:{password}@{host}:{port}";

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
        var connectionString = string.IsNullOrEmpty(username)
            ? $"mongodb://{host}:{port}"
            : $"mongodb://{username}:{password}@{host}:{port}";

        var client = new MongoClient(connectionString);
        var db = client.GetDatabase(database);

        // Parse the query (expected format: collectionName.find({...}) or similar)
        // For simplicity, we'll use RunCommandAsync
        var command = BsonDocument.Parse(query);
        var result = await db.RunCommandAsync<BsonDocument>(command);

        return result.ToJson();
    }

    private async Task<List<TableSchema>> GetMongoDbStructuredSchemaAsync(string host, int port, string database, string username, string password)
    {
        var connectionString = string.IsNullOrEmpty(username)
            ? $"mongodb://{host}:{port}"
            : $"mongodb://{username}:{password}@{host}:{port}";

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

    #region Helpers

    private async Task<string> DataReaderToJsonAsync(IDataReader reader)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var columns = new List<string>();
        var rows = new List<Dictionary<string, object?>>();

        // Get column names
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(reader.GetName(i));
        }

        // Get rows
        while (await Task.Run(() => reader.Read()))
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                row[reader.GetName(i)] = value == DBNull.Value ? null : value;
            }
            rows.Add(row);
        }

        stopwatch.Stop();

        var result = new
        {
            columns,
            rows,
            rowCount = rows.Count,
            executionTimeMs = (int)stopwatch.ElapsedMilliseconds
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    #endregion
}
