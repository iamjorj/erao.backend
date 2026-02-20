namespace Erao.Core.DTOs.Connector;

public class CreateConnectorRequest
{
    public string Name { get; set; } = string.Empty;
    public int ConnectorType { get; set; }
    public Dictionary<string, string> Credentials { get; set; } = new();
}
