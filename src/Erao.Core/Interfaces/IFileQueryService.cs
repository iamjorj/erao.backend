namespace Erao.Core.Interfaces;

public interface IFileQueryService
{
    /// <summary>
    /// Executes a SQL query against file data loaded into an in-memory SQLite database.
    /// </summary>
    /// <param name="parsedContentJson">The file's parsed content as a JSON array of objects</param>
    /// <param name="schemaInfoJson">The file's schema information as JSON</param>
    /// <param name="query">The SQL SELECT query to execute</param>
    /// <returns>Query results as JSON string with columns, rows, rowCount</returns>
    Task<string> ExecuteQueryAsync(string parsedContentJson, string schemaInfoJson, string query);

    /// <summary>
    /// Executes multiple SQL queries against file data using a single in-memory SQLite database.
    /// Loads data once, runs all queries, returns list of results.
    /// </summary>
    Task<List<string>> ExecuteQueriesAsync(string parsedContentJson, string schemaInfoJson, List<string> queries);

    /// <summary>
    /// Builds a SQLite-compatible schema description from file schema info.
    /// </summary>
    string BuildSchemaDescription(string schemaInfoJson, string tableName, int? rowCount);

    /// <summary>
    /// Builds a SQLite-compatible schema description with sample data rows for AI context.
    /// </summary>
    string BuildSchemaDescription(string schemaInfoJson, string tableName, int? rowCount, string? parsedContentJson);

    /// <summary>
    /// Gets preview data from a file (first N rows).
    /// </summary>
    Task<string> GetPreviewDataAsync(Guid fileId, int limit = 50);

    /// <summary>
    /// Gets column statistics for a file column.
    /// </summary>
    Task<string> GetColumnStatsAsync(Guid fileId, string columnName);

    /// <summary>
    /// Gets file schema information.
    /// </summary>
    Task<string> GetSchemaAsync(Guid fileId);

    /// <summary>
    /// Gets overall file statistics.
    /// </summary>
    Task<string> GetFileStatsAsync(Guid fileId);
}
