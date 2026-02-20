namespace Erao.Core.DTOs.Connector;

public class ConnectorMetadataDto
{
    public int ConnectorType { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IconSlug { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public List<CredentialFieldDefinition> CredentialFields { get; set; } = new();
    public List<string> DataTables { get; set; } = new();
}

public class CredentialFieldDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = "text"; // text | password | url
    public string Placeholder { get; set; } = string.Empty;
    public bool Required { get; set; } = true;
    public string? HelpText { get; set; }
}
