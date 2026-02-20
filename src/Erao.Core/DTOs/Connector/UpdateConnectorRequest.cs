namespace Erao.Core.DTOs.Connector;

public class UpdateConnectorRequest
{
    public string? Name { get; set; }
    public Dictionary<string, string>? Credentials { get; set; }
}
