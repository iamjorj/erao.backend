namespace Erao.Core.DTOs.Chat;

public class SqlValidationResult
{
    public bool IsValid { get; set; }
    public List<SchemaIssue> Issues { get; set; } = new();

    public string BuildCorrectionHint()
    {
        if (Issues.Count == 0) return string.Empty;

        var lines = new List<string> { "SCHEMA ISSUES FOUND:" };
        foreach (var issue in Issues)
        {
            var line = issue.Type == "unknown_table"
                ? $"- Table \"{issue.Referenced}\" does not exist."
                : $"- Column \"{issue.Referenced}\" does not exist in table \"{issue.InTable}\".";

            if (!string.IsNullOrEmpty(issue.Suggestion))
            {
                line += $" Did you mean \"{issue.Suggestion}\"?";
            }

            lines.Add(line);
        }

        lines.Add("");
        lines.Add("Fix the SQL using the correct names from the schema.");
        return string.Join("\n", lines);
    }
}

public class SchemaIssue
{
    public string Type { get; set; } = string.Empty;       // "unknown_column" or "unknown_table"
    public string Referenced { get; set; } = string.Empty;  // what AI wrote
    public string? Suggestion { get; set; }                 // closest match (Levenshtein <= 2)
    public string? InTable { get; set; }                    // context table
}
