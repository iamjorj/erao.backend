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
    /// Builds a SQLite-compatible schema description from file schema info.
    /// </summary>
    string BuildSchemaDescription(string schemaInfoJson, string tableName, int? rowCount);
}
