using System.Text.RegularExpressions;
using Erao.Core.DTOs.Chat;

namespace Erao.Application.Services;

public static class SqlSchemaValidator
{
    /// <summary>
    /// Parse schema text into lookup: { "tableName" -> {"col1", "col2"} }
    /// Handles both database schemas (Table: "name") and file schemas (single "data" table).
    /// </summary>
    public static Dictionary<string, HashSet<string>> ParseSchemaToLookup(string schemaText, bool isFileMode)
    {
        var lookup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(schemaText))
            return lookup;

        if (isFileMode)
        {
            // File mode: all columns belong to the "data" table
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Match column names in various formats:
            // - "ColumnName" (type) or - "ColumnName": ...
            var colPattern = new Regex(@"[-•]\s*""([^""]+)""", RegexOptions.Compiled);
            foreach (Match match in colPattern.Matches(schemaText))
            {
                columns.Add(match.Groups[1].Value);
            }

            // Also try: Column: "Name" pattern
            var colPattern2 = new Regex(@"Column:\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            foreach (Match match in colPattern2.Matches(schemaText))
            {
                columns.Add(match.Groups[1].Value);
            }

            // Also try bare column names after bullet points or in schema lines
            // Pattern: lines like "  ColumnName (TEXT)" or "  ColumnName — NUMERIC"
            var colPattern3 = new Regex(@"^\s*[-•*]\s*[""']?([A-Za-z_][\w\s]*?)[""']?\s*[\(—–\-:|\[]", RegexOptions.Compiled | RegexOptions.Multiline);
            foreach (Match match in colPattern3.Matches(schemaText))
            {
                var col = match.Groups[1].Value.Trim();
                if (col.Length > 0 && col.Length < 100)
                {
                    columns.Add(col);
                }
            }

            if (columns.Count > 0)
            {
                lookup["data"] = columns;
            }
        }
        else
        {
            // Database mode: parse table/column structure
            // Match table headers like: Table: "public"."employees" or Table: "employees"
            var tablePattern = new Regex(
                @"Table:\s*(?:""[^""]*""\s*\.\s*)?""([^""]+)""",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            string? currentTable = null;

            foreach (var line in schemaText.Split('\n'))
            {
                var tableMatch = tablePattern.Match(line);
                if (tableMatch.Success)
                {
                    currentTable = tableMatch.Groups[1].Value;
                    if (!lookup.ContainsKey(currentTable))
                    {
                        lookup[currentTable] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                    continue;
                }

                if (currentTable != null)
                {
                    // Match column lines: - "column_name" (type) or   "column_name" type
                    var colMatch = Regex.Match(line, @"[-•]\s*""([^""]+)""");
                    if (colMatch.Success)
                    {
                        lookup[currentTable].Add(colMatch.Groups[1].Value);
                    }
                    else
                    {
                        // Try: ColumnName (TYPE) pattern without quotes
                        var colMatch2 = Regex.Match(line, @"^\s+(\w+)\s+\(");
                        if (colMatch2.Success && colMatch2.Groups[1].Value.ToUpperInvariant() != "TABLE")
                        {
                            lookup[currentTable].Add(colMatch2.Groups[1].Value);
                        }
                    }
                }
            }
        }

        return lookup;
    }

    /// <summary>
    /// Extract quoted identifiers from SQL using regex.
    /// Handles: "name" (PostgreSQL/SQLite), `name` (MySQL), [name] (SQL Server)
    /// </summary>
    public static HashSet<string> ExtractIdentifiers(string sql)
    {
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Double-quoted identifiers: "identifier"
        foreach (Match match in Regex.Matches(sql, @"""([^""]+)"""))
        {
            identifiers.Add(match.Groups[1].Value);
        }

        // Backtick identifiers: `identifier`
        foreach (Match match in Regex.Matches(sql, @"`([^`]+)`"))
        {
            identifiers.Add(match.Groups[1].Value);
        }

        // Bracket identifiers: [identifier]
        foreach (Match match in Regex.Matches(sql, @"\[([^\]]+)\]"))
        {
            identifiers.Add(match.Groups[1].Value);
        }

        return identifiers;
    }

    /// <summary>
    /// Cross-check SQL identifiers against schema, return issues with fuzzy suggestions.
    /// </summary>
    public static SqlValidationResult Validate(string sql, Dictionary<string, HashSet<string>> schemaLookup)
    {
        var result = new SqlValidationResult { IsValid = true };

        if (schemaLookup.Count == 0)
            return result;

        var identifiers = ExtractIdentifiers(sql);
        if (identifiers.Count == 0)
            return result;

        // Build a set of all known names (tables + all columns)
        var allTableNames = new HashSet<string>(schemaLookup.Keys, StringComparer.OrdinalIgnoreCase);
        var allColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cols in schemaLookup.Values)
        {
            foreach (var col in cols)
            {
                allColumnNames.Add(col);
            }
        }

        // Common SQL keywords/functions to ignore
        var sqlKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "now", "data", "null", "true", "false", "date", "time", "timestamp",
            "text", "integer", "real", "numeric", "boolean", "varchar", "char",
            "count", "sum", "avg", "min", "max", "round", "coalesce", "nullif",
            "cast", "case", "when", "then", "else", "end", "as", "asc", "desc",
            "limit", "offset", "group", "order", "having", "where", "from",
            "select", "distinct", "join", "left", "right", "inner", "outer",
            "on", "and", "or", "not", "in", "between", "like", "ilike", "is",
            "percent_rank", "rank", "row_number", "dense_rank", "over", "partition",
            "with", "recursive", "union", "all", "except", "intersect", "exists",
            "strftime", "replace", "substr", "length", "trim", "lower", "upper",
            "abs", "total", "typeof", "ifnull", "iif", "instr", "hex", "zeroblob"
        };

        foreach (var identifier in identifiers)
        {
            // Skip SQL keywords
            if (sqlKeywords.Contains(identifier))
                continue;

            // Skip if it's a known table name
            if (allTableNames.Contains(identifier))
                continue;

            // Skip if it's a known column in any table
            if (allColumnNames.Contains(identifier))
                continue;

            // Skip identifiers that look like aliases (short, common patterns)
            if (identifier.Length <= 2)
                continue;

            // This identifier is not in the schema — find closest match
            var issue = new SchemaIssue { Referenced = identifier };

            // Check if it's close to a table name
            string? bestTableMatch = FindClosestMatch(identifier, allTableNames, maxDistance: 2);
            if (bestTableMatch != null)
            {
                issue.Type = "unknown_table";
                issue.Suggestion = bestTableMatch;
                result.Issues.Add(issue);
                result.IsValid = false;
                continue;
            }

            // Check if it's close to a column name
            string? bestColumnMatch = null;
            string? inTable = null;

            foreach (var (tableName, columns) in schemaLookup)
            {
                var match = FindClosestMatch(identifier, columns, maxDistance: 2);
                if (match != null)
                {
                    bestColumnMatch = match;
                    inTable = tableName;
                    break;
                }
            }

            if (bestColumnMatch != null)
            {
                issue.Type = "unknown_column";
                issue.Suggestion = bestColumnMatch;
                issue.InTable = inTable;
                result.Issues.Add(issue);
                result.IsValid = false;
            }
            else
            {
                // No close match found — might be an alias or computed column, skip it
                // Only flag if it looks like it could be a real column name (has underscore or matches column naming pattern)
                if (identifier.Contains('_') || identifier.Any(char.IsUpper))
                {
                    issue.Type = "unknown_column";
                    issue.Suggestion = null;
                    result.Issues.Add(issue);
                    result.IsValid = false;
                }
            }
        }

        return result;
    }

    private static string? FindClosestMatch(string input, IEnumerable<string> candidates, int maxDistance)
    {
        string? best = null;
        var bestDistance = maxDistance + 1;

        foreach (var candidate in candidates)
        {
            var distance = LevenshteinDistance(input.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance <= maxDistance && distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var matrix = new int[a.Length + 1, b.Length + 1];

        for (var i = 0; i <= a.Length; i++) matrix[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) matrix[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }

        return matrix[a.Length, b.Length];
    }
}
