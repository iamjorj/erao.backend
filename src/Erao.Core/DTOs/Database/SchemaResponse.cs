namespace Erao.Core.DTOs.Database;

public class SchemaResponse
{
    public string DatabaseName { get; set; } = string.Empty;
    public string DatabaseType { get; set; } = string.Empty;
    public List<TableSchema> Tables { get; set; } = new();
    public DateTime? CachedAt { get; set; }

    // Keep raw schema for backward compatibility
    public string RawSchema { get; set; } = string.Empty;
}

public class TableSchema
{
    public string Name { get; set; } = string.Empty;
    public string? Schema { get; set; }
    public List<ColumnSchema> Columns { get; set; } = new();
    public List<PrimaryKeyInfo> PrimaryKeys { get; set; } = new();
    public List<ForeignKeyInfo> ForeignKeys { get; set; } = new();
    public List<IndexInfo> Indexes { get; set; } = new();
    public long? RowCount { get; set; }
}

public class ColumnSchema
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public string? DefaultValue { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsForeignKey { get; set; }
    public int? MaxLength { get; set; }
    public int? Precision { get; set; }
    public int? Scale { get; set; }
    public bool IsIdentity { get; set; }
}

public class PrimaryKeyInfo
{
    public string Name { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = new();
}

public class ForeignKeyInfo
{
    public string Name { get; set; } = string.Empty;
    public string Column { get; set; } = string.Empty;
    public string ReferencedTable { get; set; } = string.Empty;
    public string ReferencedColumn { get; set; } = string.Empty;
    public string? OnDelete { get; set; }
    public string? OnUpdate { get; set; }
}

public class IndexInfo
{
    public string Name { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = new();
    public bool IsUnique { get; set; }
    public bool IsClustered { get; set; }
}
